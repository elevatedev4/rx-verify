/**
 * BUILD-TIME ONLY script. Run manually by a maintainer to refresh the
 * local wholesaler-catalog dataset — never invoked by the app at runtime
 * (see src/drug/catalog.ts CatalogDataProvider, which only ever reads
 * the committed output file, data/catalog-data.json.gz).
 *
 * SOURCE: the pharmacy owner's own McKesson wholesaler catalog export
 * (.xlsx), supplied for use inside his own private verification tool.
 * This is NOT public data (see src/drug/catalog-data-format.ts's header)
 * — the source .xlsx is NEVER read from or written into this repo; it's
 * a local file path the maintainer points this script at. Only the
 * DERIVED data/catalog-data.json.gz is committed.
 *
 * Usage:
 *   npx tsx scripts/build-catalog-data.ts /path/to/catalog-export.xlsx
 *
 * Requires `unzip` on PATH (an .xlsx is a zip archive of OOXML parts —
 * same tool this repo already shells out to in build-drug-data.ts, no
 * new dependency). No network access.
 *
 * XLSX PARSING APPROACH: rather than add an npm dependency for a
 * one-file, one-sheet, plain-tabular workbook, this reads the two OOXML
 * parts that matter (xl/sharedStrings.xml, xl/worksheets/sheet1.xml)
 * directly via `unzip -p` and parses their small, regular XML shape with
 * targeted regexes (see parseSharedStringsXml/parseSheetRowsXml) — no
 * XML DOM, no styles/merged-cell/formula handling, since the source
 * export is a plain single-sheet data dump (verified against a real
 * 35,081-row export: no merged cells, no formulas, every populated cell
 * is either a shared-string reference or a bare numeric literal).
 */

import { execFileSync } from 'node:child_process';
import { writeFileSync } from 'node:fs';
import path from 'node:path';
import { pathToFileURL } from 'node:url';
import { gzipSync } from 'node:zlib';
import { parseNdc } from '../src/drug/index.js';
import { normalizeCatalogText, catalogNameIndexKeys } from '../src/drug/catalog.js';
import type { CatalogData, CatalogEntry } from '../src/drug/catalog-data-format.js';

const OUTPUT_PATH = path.join(import.meta.dirname, '..', 'data', 'catalog-data.json.gz');

export const DEFAULT_ATTRIBUTION =
  'Derived from the pharmacy’s own wholesaler catalog export. Internal use only within ' +
  'this private verification tool — the GCN (Generic Code Number) field is First DataBank ' +
  'proprietary reference data and must never be redistributed.';

/** Parses xl/sharedStrings.xml's <si><t>...</t></si> entries, in order
 * (their array index IS the shared-string id referenced by <c t="s">). */
export function parseSharedStringsXml(xml: string): string[] {
  const items: string[] = [];
  const siRe = /<si>(.*?)<\/si>/gs;
  let m: RegExpExecArray | null;
  while ((m = siRe.exec(xml))) {
    const inner = m[1] as string;
    // A shared-string entry can contain multiple <t> runs (rich text) --
    // concatenate them, matching how Excel displays the cell.
    const parts = [...inner.matchAll(/<t[^>]*>(.*?)<\/t>/gs)].map((tm) => decodeXmlEntities(tm[1] ?? ''));
    items.push(parts.join(''));
  }
  return items;
}

function decodeXmlEntities(s: string): string {
  return s
    .replace(/&lt;/g, '<')
    .replace(/&gt;/g, '>')
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, '&');
}

/**
 * Parses xl/worksheets/sheet1.xml into an array of row objects keyed by
 * COLUMN LETTER ("A", "B", ...), resolving shared-string cells (t="s")
 * against `sharedStrings` and leaving other cell types (bare numeric
 * literals) as their literal text. A blank cell is simply absent from
 * the row object (never an empty string), matching how Excel omits
 * genuinely empty cells from the underlying XML.
 *
 * The cell regex is written to correctly handle a SELF-CLOSING <v/>
 * (an explicitly blank-but-typed cell, seen in the real export for
 * blank GCN cells) without over-matching into the next cell's content —
 * see the two-step (find the `<c>...</c>` span first, THEN look for
 * `<v>` inside it) design; an earlier single-regex draft of this let a
 * `<v/>` self-closing tag's non-greedy `(.*?)<\/v>` search past the
 * empty cell entirely and swallow a subsequent cell's real value.
 */
export function parseSheetRowsXml(xml: string, sharedStrings: string[]): Array<Record<string, string>> {
  const rows: Array<Record<string, string>> = [];
  const rowRe = /<row[^>]*>(.*?)<\/row>/gs;
  const cellRe = /<c r="([A-Z]+)\d+"([^>]*?)(?:\/>|>(.*?)<\/c>)/gs;
  const vRe = /<v[^>]*>(.*?)<\/v>|<v\s*\/>/s;
  let rowMatch: RegExpExecArray | null;
  while ((rowMatch = rowRe.exec(xml))) {
    const rowXml = rowMatch[1] as string;
    const row: Record<string, string> = {};
    cellRe.lastIndex = 0;
    let cellMatch: RegExpExecArray | null;
    while ((cellMatch = cellRe.exec(rowXml))) {
      const col = cellMatch[1] as string;
      const attrs = cellMatch[2] as string;
      const inner = cellMatch[3];
      if (inner === undefined) continue; // self-closing <c .../> -- genuinely blank cell
      const vMatch = vRe.exec(inner);
      if (!vMatch || vMatch[1] === undefined) continue; // no <v> or a self-closing <v/> -- blank
      const rawValue = vMatch[1];
      const typeMatch = /\st="([a-z]+)"/.exec(attrs);
      const isSharedString = typeMatch?.[1] === 's';
      const value = isSharedString ? sharedStrings[Number(rawValue)] : rawValue;
      if (value !== undefined) row[col] = decodeXmlEntities(value);
    }
    rows.push(row);
  }
  return rows;
}

export interface CatalogSourceRow {
  supplierName?: string;
  brandGeneric?: string;
  description?: string;
  ndc?: string;
  shippingSize?: string;
  gcn?: string;
  deaSchedule?: string;
}

const HEADER_TO_FIELD: Record<string, keyof CatalogSourceRow> = {
  'supplier name': 'supplierName',
  'brand/generic': 'brandGeneric',
  description: 'description',
  ndc: 'ndc',
  'shipping size': 'shippingSize',
  gcn: 'gcn',
  'dea schedule': 'deaSchedule'
};

/**
 * Maps raw column-letter-keyed rows to named CatalogSourceRow objects,
 * using ROW 0 as the header (matched case-insensitively against
 * HEADER_TO_FIELD) rather than hardcoding column letters — resilient to
 * the wholesaler reordering columns in a future export. A header cell
 * that doesn't match any known field is silently ignored (forward
 * compatible with an export that adds a column this script doesn't use
 * yet) rather than erroring.
 */
export function mapRowsToCatalogSourceRows(allRows: Array<Record<string, string>>): CatalogSourceRow[] {
  if (allRows.length === 0) return [];
  const header = allRows[0] as Record<string, string>;
  const colToField = new Map<string, keyof CatalogSourceRow>();
  for (const [col, headerText] of Object.entries(header)) {
    const field = HEADER_TO_FIELD[headerText.trim().toLowerCase()];
    if (field) colToField.set(col, field);
  }

  const out: CatalogSourceRow[] = [];
  for (const row of allRows.slice(1)) {
    const mapped: CatalogSourceRow = {};
    for (const [col, value] of Object.entries(row)) {
      const field = colToField.get(col);
      if (field) mapped[field] = value;
    }
    out.push(mapped);
  }
  return out;
}

export interface BuildCatalogStats {
  totalRows: number;
  skippedMissingNdc: number;
  skippedUnparseableNdc: number;
  uniqueNdcCount: number;
  duplicateNdcRowsSkipped: number;
  uniqueGcnCount: number;
  missingGcnCount: number;
  nameKeyCount: number;
}

export interface BuildCatalogResult {
  data: CatalogData;
  stats: BuildCatalogStats;
}

/**
 * Pure in-memory transform: parsed catalog source rows -> the full
 * CatalogData shape. No file access, so this is what tests call directly
 * against small SYNTHETIC (invented) fixture rows — never real catalog
 * data (see this repo's README "SYNTHETIC DATA ONLY" exception, which
 * does NOT cover this proprietary wholesaler data the way it covers
 * openFDA/RxNorm's public data).
 */
export function buildCatalogDataset(
  rows: CatalogSourceRow[],
  opts: { generatedAt?: string; source?: string; attribution?: string } = {}
): BuildCatalogResult {
  const ndcIndex: Record<string, CatalogEntry> = {};
  const nameIndex: Record<string, string[]> = {};
  let skippedMissingNdc = 0;
  let skippedUnparseableNdc = 0;
  let duplicateNdcRowsSkipped = 0;
  let missingGcnCount = 0;

  for (const row of rows) {
    if (!row.ndc || !row.ndc.trim()) {
      skippedMissingNdc++;
      continue;
    }
    const parsed = parseNdc(row.ndc.trim());
    if (!parsed) {
      skippedUnparseableNdc++;
      continue;
    }
    const ndc11 = parsed.normalized11;
    if (ndc11 in ndcIndex) {
      // Real export has ~216 duplicate NDCs (same product re-listed,
      // e.g. under a second supplier record) -- keep the first, same
      // "first wins" convention as build-drug-data.ts's ndcIndex.
      duplicateNdcRowsSkipped++;
      continue;
    }

    const gcn = row.gcn && row.gcn.trim() ? row.gcn.trim() : null;
    if (!gcn) missingGcnCount++;
    const brandGenericRaw = row.brandGeneric?.trim().toLowerCase();
    const brandGeneric: CatalogEntry['brandGeneric'] =
      brandGenericRaw === 'brand' ? 'brand' : brandGenericRaw === 'generic' ? 'generic' : null;
    const description = (row.description ?? '').trim();

    const entry: CatalogEntry = {
      gcn,
      description,
      brandGeneric,
      deaSchedule: row.deaSchedule?.trim() || null
    };
    ndcIndex[ndc11] = entry;

    if (description) {
      const normalized = normalizeCatalogText(description);
      // Index EVERY leading-prefix key of this description, not just the
      // full string -- see catalogNameIndexKeys' doc (src/drug/catalog.ts)
      // for why a single full-string key would make most of the catalog
      // unreachable from a realistic (shorter, non-pack-code-restating)
      // entered name.
      for (const key of catalogNameIndexKeys(normalized)) {
        const bucket = nameIndex[key] ?? (nameIndex[key] = []);
        if (bucket[bucket.length - 1] !== ndc11) bucket.push(ndc11);
      }
    }
  }

  const gcnCounts: Record<string, number> = {};
  for (const entry of Object.values(ndcIndex)) {
    if (entry.gcn) gcnCounts[entry.gcn] = (gcnCounts[entry.gcn] ?? 0) + 1;
  }

  const data: CatalogData = {
    generatedAt: opts.generatedAt ?? new Date().toISOString(),
    source: opts.source ?? 'wholesaler catalog export',
    attribution: opts.attribution ?? DEFAULT_ATTRIBUTION,
    ndcIndex,
    nameIndex,
    gcnCounts
  };

  return {
    data,
    stats: {
      totalRows: rows.length,
      skippedMissingNdc,
      skippedUnparseableNdc,
      uniqueNdcCount: Object.keys(ndcIndex).length,
      duplicateNdcRowsSkipped,
      uniqueGcnCount: Object.keys(gcnCounts).length,
      missingGcnCount,
      nameKeyCount: Object.keys(nameIndex).length
    }
  };
}

function main(): void {
  const inputPath = process.argv[2];
  if (!inputPath) {
    console.error('Usage: npx tsx scripts/build-catalog-data.ts /path/to/catalog-export.xlsx');
    process.exitCode = 1;
    return;
  }

  console.log(`Reading ${inputPath} ...`);
  const sharedStringsXml = execFileSync('unzip', ['-p', inputPath, 'xl/sharedStrings.xml'], {
    maxBuffer: 1024 * 1024 * 1024
  }).toString('utf8');
  const sheetXml = execFileSync('unzip', ['-p', inputPath, 'xl/worksheets/sheet1.xml'], {
    maxBuffer: 1024 * 1024 * 1024
  }).toString('utf8');

  const sharedStrings = parseSharedStringsXml(sharedStringsXml);
  const allRows = parseSheetRowsXml(sheetXml, sharedStrings);
  const sourceRows = mapRowsToCatalogSourceRows(allRows);
  console.log(`Parsed ${sourceRows.length} data rows (${sharedStrings.length} shared strings).`);

  const { data, stats } = buildCatalogDataset(sourceRows, { generatedAt: new Date().toISOString() });

  const compressed = gzipSync(Buffer.from(JSON.stringify(data), 'utf8'), { level: 9 });
  writeFileSync(OUTPUT_PATH, compressed);
  console.log(`Wrote ${OUTPUT_PATH} (${(compressed.length / 1024 / 1024).toFixed(2)} MB gzipped)`);
  console.log(
    `${stats.uniqueNdcCount} NDCs indexed (skipped ${stats.skippedMissingNdc} missing-NDC rows, ` +
      `${stats.skippedUnparseableNdc} unparseable NDCs, ${stats.duplicateNdcRowsSkipped} duplicate NDCs), ` +
      `${stats.uniqueGcnCount} distinct GCNs (${stats.missingGcnCount} rows with no GCN), ` +
      `${stats.nameKeyCount} normalized description keys.`
  );
}

const isMainModule = import.meta.url === pathToFileURL(process.argv[1] ?? '').href;
if (isMainModule) {
  main();
}
