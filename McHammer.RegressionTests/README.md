# Regression tests: PRTG group creation responses

Run without contacting PRTG:

```powershell
dotnet run --project M:\McHammer\McHammer.RegressionTests -c Release
```

The console harness injects fake HttpMessageHandlers into PrtgGroupSyncService.
It covers numeric/string IDs, empty 201/204 responses, missing IDs, delayed visibility,
ambiguous groups, partial name matches, malformed JSON in v1/v2, and HTTP errors.
Every case asserts that the creation POST occurs exactly once.
A failing assertion terminates the process with a nonzero exit code.

The separately published corrected developer CLI is at:
M:\McHammer\artifacts\group-sync-fix\McHammer.Dev.exe

Close the old CLI before launching this version. The regular Debug output could not
be updated while McHammer.Dev held its DLL open. No live synchronization was run
as part of these tests. The existing network-directory change is retained.

Group lookup after a creation acknowledgement polls for up to 60 seconds (30 read attempts).
The timeout is also covered by a regression test; no creation POST is repeated.

