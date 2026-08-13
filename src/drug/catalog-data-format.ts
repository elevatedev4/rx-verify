/**
 * Shared shape for the bundled wholesaler-catalog dataset
 * (data/catalog-data.json.gz). Used by BOTH:
 *  - scripts/build-catalog-data.ts (build-time, no network — reads the
 *    pharmacy's own wholesaler catalog export, a local file never
 *    committed to this repo)
 *  - src/drug/catalog.ts CatalogDataProvider (runtime, reads only the
 *    derived .gz)
 *
 * SOURCE / LICENSING NOTE: this data is derived from the pharmacy
 * owner's own McKesson wholesaler catalog export (Supplier Name,
 * Brand/Generic, Description, NDC, Shipping Size, GCN, Dea Schedule).
 * The GCN (Generic Code Number) field is First DataBank (FDB)
 * proprietary reference data, owner-supplied for use inside his own
 * pharmacy's private verification tool — NOT public domain like the
 * openFDA/RxNorm datasets. data/catalog-data.json.gz is INTERNAL USE
 * ONLY within this tool and must never be redistributed. The source
 * .xlsx itself is never committed (see scripts/build-catalog-data.ts).
 */

export interface CatalogEntry {
  /** Generic Code Number — FDB's cross-labeler generic-equivalence
   * grouping key. Null when the source row had no GCN (some brand/
   * device rows in the catalog carry none). */
  gcn: string | null;
  /** Wholesaler's free-text, heavily abbreviated product description,
   * UNMODIFIED (e.g. "ABACAV LAM TB 600 300MG CIP30@") — kept for
   * display/debugging; matching uses the separately normalized
   * nameIndex below, never this raw string directly. */
  description: string;
  brandGeneric: 'brand' | 'generic' | null;
  /** DEA schedule as a string ("0" = non-controlled, "2".."5" =
   * schedule II-V) — kept as the catalog's own raw value, never
   * interpreted/compared by this feature. */
  deaSchedule: string | null;
}

export interface CatalogData {
  generatedAt: string;
  /** Deliberately vague per the internal-use note above — never a URL,
   * never identifies the specific wholesaler account. */
  source: string;
  attribution: string;
  /** normalized 11-digit package NDC -> entry. */
  ndcIndex: Record<string, CatalogEntry>;
  /**
   * Normalized description (see normalizeCatalogText in
   * src/drug/catalog.ts) -> every ndc11 whose row produced that exact
   * normalized text. Mirrors LocalDrugData.nameIndex's
   * "candidates, not pre-resolved" shape — see resolveGcnByName in
   * src/drug/catalog.ts for the disambiguation step that narrows this
   * down to at most one GCN.
   */
  nameIndex: Record<string, string[]>;
  /** gcn -> number of distinct NDCs in the catalog sharing it. Purely
   * informational (e.g. for the build report's coverage stats) — never
   * used to gate a verdict. */
  gcnCounts: Record<string, number>;
}
