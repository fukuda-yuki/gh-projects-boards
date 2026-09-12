# Probe dependencies and redistribution review

This is an artifact inventory for the isolated probe, not legal advice or approval of a finished distributor. Retain the original terms in `notices/` with any redistributed probe payload. The lockfile records dependency relationships and content hashes. Framework/runtime packs supplied by the .NET SDK also have their own MIT/third-party notices; preserve those in a distributable layout.

| Artifact | Resolved version | Terms |
| --- | --- | --- |
| WinUI.TableView | 1.4.1 | MIT, copyright/permission notice retained |
| Microsoft.WindowsAppSDK | 1.8.260804001 | Packaged Microsoft Windows App SDK license, not a blanket MIT assertion |
| Microsoft.WindowsAppSDK.AI | 1.8.79 | Included license/notice |
| Microsoft.WindowsAppSDK.Base | 1.8.251216001 | Included license/notice |
| Microsoft.WindowsAppSDK.DWrite | 1.8.25122902 | Included license/notice |
| Microsoft.WindowsAppSDK.Foundation | 1.8.260803002 | Included license/notice |
| Microsoft.WindowsAppSDK.InteractiveExperiences | 1.8.260708001 | Included license/notice |
| Microsoft.WindowsAppSDK.ML | 1.8.2197 | Included license/notice |
| Microsoft.WindowsAppSDK.Runtime | 1.8.260804001 | Included license/notice |
| Microsoft.WindowsAppSDK.Widgets | 1.8.251231004 | Included license/notice |
| Microsoft.WindowsAppSDK.WinUI | 1.8.260803003 | Included license/notice |
| Microsoft.Web.WebView2 | 1.0.3179.45 | Included Microsoft license; no WebView is used by this probe |
| Microsoft.Windows.SDK.BuildTools | 10.0.26100.4654 | Windows SDK terms: https://aka.ms/WinSDKLicenseURL |
| Microsoft.Windows.SDK.BuildTools.MSIX | 1.7.20250829.1 | Included SDK license |
| System.Numerics.Tensors | 9.0.0 | MIT |

The Windows App SDK package license permits development/testing copies for Windows and defines redistributable files placed with the application. Its distribution conditions, third-party notices, and Windows-only terms still apply. The TableView MIT terms do not impose a company-size/revenue eligibility condition. No required paid component or company-size subscription gate was identified in the inspected direct package terms. This does not assert all indirect artifacts are MIT. Complete the end-user license/notice and clean-machine audit before product distribution under #13; the SDK BuildTools license is referenced rather than duplicated here.

Sources: [TableView v1.4.1](https://github.com/w-ahmad/WinUI.TableView/tree/v1.4.1), [Windows App SDK 1.8.11 release](https://github.com/microsoft/WindowsAppSDK/releases/tag/v1.8.11), and the exact NuGet package metadata and terms retained with this probe. Repository source licenses alone do not replace binary-package terms.

The maintainer owns version servicing, transitive dependency/license review, notice preservation and re-execution of input/activation tests. The existing test-only NUnit/FlaUI packages are unchanged. Microsoft `winui` plugin 0.6.1 is MIT-licensed development tooling, outside the runtime dependency graph; its unsigned analyzer is not referenced or executed by this prototype.
