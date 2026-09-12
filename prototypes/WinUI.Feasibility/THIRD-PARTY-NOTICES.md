# Probe dependencies and redistribution review

This is an artifact inventory for the isolated probe, not legal advice or approval of a finished distributor. Retain the original terms in `notices/` with any redistributed probe payload. The lockfile records dependency relationships and content hashes. Framework/runtime packs supplied by the .NET SDK also have their own MIT/third-party notices; preserve those in a distributable layout.

| Artifact | Resolved version | Terms |
| --- | --- | --- |
| WinUI.TableView | 1.4.1 | MIT, copyright/permission notice retained |
| Microsoft.WindowsAppSDK | 1.8.260804001 | Packaged Microsoft Windows App SDK license, not a blanket MIT assertion |
| Microsoft.WindowsAppSDK.AI | 1.8.79 | Included license/notice |
| Microsoft.WindowsAppSDK.Base | 1.8.251216001 | Included license/notice |
| Microsoft.WindowsAppSDK.DWrite | 1.8.25122902 | Included license; see component redistribution boundary below |
| Microsoft.WindowsAppSDK.Foundation | 1.8.260803002 | Included license/notice |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 1.8.260708001 | Included license/notice |
| Microsoft.WindowsAppSDK.ML | 1.8.2197 | Included license/notice |
| Microsoft.WindowsAppSDK.Runtime | 1.8.260804001 | Included license/notice |
| Microsoft.WindowsAppSDK.Widgets | 1.8.251231004 | Included license; see component redistribution boundary below |
| Microsoft.WindowsAppSDK.WinUI | 1.8.260803003 | Included license/notice |
| Microsoft.Web.WebView2 | 1.0.3179.45 | Included Microsoft license; no WebView is used by this probe |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | Original SDK license RTF retained in `notices/`; source: https://aka.ms/WinSDKLicenseURL |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.20250829.1 | Included SDK license |
| System.Numerics.Tensors | 9.0.0 | MIT |

The Windows App SDK package license permits development/testing copies for Windows and defines redistributable files placed with the application. Its distribution conditions, third-party notices, and Windows-only terms still apply. The TableView MIT terms do not impose a company-size/revenue eligibility condition. No required paid component or company-size subscription gate was identified for local development/testing in the inspected package terms. This does not assert all indirect artifacts are MIT or approve external binary redistribution.

The BuildTools nuspec requires license acceptance and points to the SDK license redirect; it includes no separate license/notice file. The retained `notices/Microsoft.Windows.SDK.BuildTools-10.0.26100.4654-sdk_license.rtf` is the unchanged [official Microsoft RTF](https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf), retrieved through that redirect on 2026-09-13, SHA256 `DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62`. The redirect is not versioned. Sections 1(a) and 1(c) permit development/testing and organizational build servers. BuildTools and BuildTools.MSIX are build tooling, not application runtime dependencies in this probe layout; their presence in the lockfile is not evidence of redistribution. Publishing SDK tools requires a separate check of the applicable utility/redistributable lists and conditions.

DWrite and Widgets have component-specific licenses whose Section 3(a) omits the general binplaced-file grant present in the parent Windows App SDK license. Their Section 1(a) permits local Windows development/testing. The self-contained output includes DWriteCore.dll and Widgets binaries even though the probe does not use widget features. Do not silently substitute the parent terms for these component terms: the applicability of the parent redistribution grant to these exact payloads remains a review question in [#22](https://github.com/fukuda-yuki/gh-projects-boards/issues/22).

The project does not automatically copy this notice inventory into the executable directory. Before distributing any binary bundle, reconcile its actual files (including SDK-supplied .NET runtime packs) with their original terms and notices, resolve the component redistribution question, and satisfy external end-user/distributor conditions. Product distribution and clean-machine validation belong to [#13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13); source notice retention alone does not satisfy that gate.

Sources: [TableView v1.4.1](https://github.com/w-ahmad/WinUI.TableView/tree/v1.4.1), [Windows App SDK 1.8.11 release](https://github.com/microsoft/WindowsAppSDK/releases/tag/v1.8.11), and the exact NuGet package metadata and terms retained with this probe. Repository source licenses alone do not replace binary-package terms.

The maintainer owns version servicing, transitive dependency/license review, notice preservation and re-execution of input/activation tests. The existing test-only NUnit/FlaUI packages are unchanged. Microsoft `winui` plugin 0.6.1 is MIT-licensed development tooling, outside the runtime dependency graph; its unsigned analyzer is not referenced or executed by this prototype.
