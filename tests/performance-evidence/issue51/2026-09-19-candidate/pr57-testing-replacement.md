## Testing

Replace PR #57's existing Testing text with the following after maintainer review. This text is prepared only; PR #57 was not edited.

---

Release validation for this PR's #53/#54 changes used .NET SDK 10.0.401 on Windows 10.0.26200. Counts below are executed/passed/failed/skipped; detailed commands, artifacts and failed attempts are retained in the owning Issues.

- #53: logic/storage/recovery regression **75/75/0/0**; final focused UI integration **6/6/0/0**. The tooltip follow-up observed a behavioral Red **1/0/1/0**, then Green **4/4/0/0**, plus final native-hover integration **1/1/0/0**. [Initial evidence](https://github.com/fukuda-yuki/gh-projects-boards/issues/53#issuecomment-5734454999), [follow-up evidence](https://github.com/fukuda-yuki/gh-projects-boards/issues/53#issuecomment-5735698874).
- #53: ordinary executable success/history/restart and mixed-result journeys **4/4/0/0**. The final mixed-result follow-up **2/2/0/0** used non-IME 960x600 and physical Japanese IME 1280x800 logical bounds at 125% scaling; screenshots were inspected. The final build retained two SDK-generated CS0436 warnings in the UI integration host and zero errors. [Source-specific execution record](https://github.com/fukuda-yuki/gh-projects-boards/issues/53#issuecomment-5735698874).
- #54: logic **4/4/0/0**, scoped native UI integration **4/4/0/0**, ordinary executable filtering/editing/Project-switch journeys **2/2/0/0**. [Evidence and retained failed attempts](https://github.com/fukuda-yuki/gh-projects-boards/issues/54#issuecomment-5734519584).

Ordinary-app tests used real WinUI, public FlaUI UIA3/native input and real checkpoint storage with an isolated external fake gh endpoint. These results are not live-GitHub or human UX acceptance. Screen-reader listening and other text-scale/high-contrast settings remain unverified. Issue #51 optimization experiments are not evidence for this PR.
