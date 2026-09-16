Readable strings extracted from Will's Macro Express file "Sales reports.mex"
(source: his EOM-reports recording scratchpad, NOT committed here — this
.mex binary contains no patient data, but the source file itself is kept
out of the repo per the brief; only this text extraction is committed).

Extracted with:
  strings -n 4 "Sales reports.mex"

Kept as reference for PioneerReportDriver.cs's step sequencing (ribbon
navigation, TAB order into date fields, F12 to run, wait times, PDF/Excel
Save As naming). Two macro groups are visible below: "Reports-EOM (new,
in process)" (the 7 in-scope financial/payments reports) and
"Reports-POS Sales" (out of scope for phase 1 — POS daily reports).

---8<--- RAW STRINGS OUTPUT BELOW ---8<---

Reports-EOM (new, in process)
{\rtf1\ansi\ansicpg1252\deff0\deflang1033{\fonttbl{\f0\fnil Tahoma;}}
\viewkind4\uc1\pard\f0\fs20 
\par }
mm'/'dd'/'yyyy
Last day of reporting month:
Center
Center
%start_date%
%start_date%
%month%
%start_date%
%day%
%start_date%
%year%
%start_date_text%
%month%-01-%year%
%start_date%
mm'-'dd'-'yy
%date_text%
Make sure Pioneer is active
Submacro-Switch to Pioneer
END Make sure Pioneer is active
End Look for correct window
*** Open financial reports tab ***
<ALT>
1000
*** End Open financial reports tab ***
*** Customer A/R Aged Trial Balance ***
Set variables
%report_run_time%
6000
%report_name%
A/R Aged Trial Balance
%report_save_name%
Customer AR Aged Trial Balance
Set up report
%date_text%
<F12>
Run report
Subreport-Run report
Run financial reports
*** End Customer A/R Aged Trial Balance ***
*** Customer A/R Control Balance ***
Set variables
%report_run_time%
6000
%report_name%
A/R Control Balance
%report_save_name%
Customer AR Control Balance
Set up report
<TAB>
<TAB>
%start_date_text%
<TAB>
<TAB>
%date_text%
<F12>
%report_run_time%
Run report
Subreport-Run report
Run financial reports
*** End Customer A/R Control Balance ***
*** Sales summary ***
Set variables
%report_run_time%
6000
%report_name%
Accrual system
%report_save_name%
Sales summary
Set up report
%start_date_text%
<TAB>
%date_text%
<F12>
%report_run_time%
Run report
Subreport-Run report
Run financial reports
*** End Sales summary ***
*** Inventory Control Balance ***
Set variables
%report_run_time%
20000
%report_name%
Inventory control balance
%report_save_name%
Inventory control balance
Set up report
%start_date_text%
<TAB>
%date_text%
<F12>
%report_run_time%
Run report
Subreport-Run report
Run financial reports
*** End Inventory Control Balance***
*** Inventory valuation ***
Set variables
%report_run_time%
15000
%report_name%
Inventory valuation
%report_save_name%
Inventory valuation
Set up report
%date_text%
<TAB>
<ARROW DOWN>
<F12>
<F12>
%report_run_time%
Run report
Subreport-Run report
Run financial reports
*** End Inventory valuation***
*** Third Party Control Balance ***
Set variables
%report_run_time%
20000
%report_name%
Third Party Control Balance
%report_save_name%
Third Party Control Balance
Set up report
%start_date_text%
<TAB>
%date_text%
<F12>
Run report
%report_run_time%
Subreport-Run report
Run financial reports
*** Third Party Control Balance***
*** Third Party Aged Trial Balance ***
Set variables
%report_run_time%
20000
%report_name%-
Third Party Reconciliation Account Aged Trial
%report_save_name%
Third Party Aged Trial Balance
Set up report
%date_text%
<F12>
Run report
%report_run_time%
Subreport-Run report
Run financial reports
*** End Third Party Aged Trial Balance***
*** Third Party Payments ***
Set variables
%report_run_time%
2500
%report_name%"
Third Party Reconciliation Payment
%report_save_name%"
Third Party Reconciliation Payment
Set up report
<ALT>
<TAB>
<TAB>
<PAGE UP>
<TAB>
<TAB>
<TAB>
<TAB>
<TAB>
%start_date_text%
<TAB>
%date_text%
<TAB>
<ALT>o
Run report
%report_run_time%
%report_name%
%report_run_time%
%filename%
%date_text%_%report_save_name%
Subreport-Save report as EXCEL
%report_name%
<ALT><F4>
Reconcile Third Party Payments
*** End Third Party Payments***
start_date
starting_date
number_days
num_days
date_text
month
year
start_date_text
report_run_time
print_time
window_name
name
find_program_name
max_try
window_open
current_try
filename
report_name
report_save_name
Make sure Pioneer is active
Runs report, saves PDF, and closes report
Runs report, saves PDF, and closes report
Runs report, saves PDF, and closes report
Runs report, saves PDF, and closes report
Runs report, saves PDF, and closes report
Runs report, saves PDF, and closes report
Runs report, saves PDF, and closes report
Saves PDF and closes report
Reports-POS Saless
{\rtf1\ansi\ansicpg1252\deff0\deflang1033{\fonttbl{\f0\fnil Tahoma;}}
\viewkind4\uc1\pard\f0\fs20 
\par }
PIONEERPHARMACY.EXE
a#gu
Prompt for date range
mm'/'dd'/'yyyy
Center
Center
%entered_date%
%days_entered%
Number of days:
Center
Center
Set initial variables for repeat
%number_days%
%days_entered%
%start_date%
%entered_date%
Run POS reports
%number_days%
%start_date%
mm'-'dd'-'yy
%date_text%
<ALT>
<TAB>
<TAB>
%date_text%
<TAB>
%date_text%
<ALT>o
<ARROW DOWN>
<ENTER>
Sales Summary Report
1500
1000
*** Check if in correct window before proceeding ***
Save as
<ALT>n
POS_%date_text%
<ENTER>
*** If duplicate, overwrite ***
Confirm Save As
*** Check if in correct window before proceeding ***
Sales Summary Report
<ALT><F4>
Search Drawers
%start_date%
%start_date%
1500
*** Run sales reports
Set initial variables for repeat
%number_days%
%days_entered%
%start_date%
%entered_date%
Open financial reports window
<ALT>
reports
%number_days%
%start_date%
mm'-'dd'-'yy
%date_text%
%date_text%
<ALT>e
%date_text%
<F12>
<F12>
1500
1300
*** Check if in correct window before proceeding ***
Save as
<ALT>n
Sales_%date_text%
<ENTER>
*** If duplicate, overwrite ***
Confirm Save As
*** Check if in correct window before proceeding ***
Accrual system
<ALT><F4>
%start_date%
%start_date%
Run Financial Reports
start_date
starting_date
number_days
num_days
date_text
in_correct_window
entered_date
days_entered
ScriptParsing.scriptToS)
##Repeat
4DF26592C2DB6CEE1ED50C45
##End Repeat
D3097489A36E83D40AAC3
