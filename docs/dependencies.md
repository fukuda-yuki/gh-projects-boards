# Dependency terms

The app pins Windows App SDK `1.8.260804001`. Its resolved package versions, NuGet content hashes and versioned package URLs are in [notices/packages.json](notices/packages.json). The adjacent original terms/notices cover those packages, with SHA256 values in [notices/hashes.json](notices/hashes.json). The app has no TableView dependency. Existing test dependencies remain in their test project files.

The package licenses govern packaged artifacts; a source repository's license is not a substitute for their terms. The inventory supports local Windows development and review, not approval to redistribute a self-contained executable directory.

| Component | Development/build use | Redistribution boundary |
| --- | --- | --- |
| Windows App SDK and component packages | Packaged Section 1(a) permits development/testing for Windows, subject to its terms | Preserve each component's original terms and notices; reconcile the actual output files before distribution |
| Windows SDK BuildTools 10.0.26100.4654 | SDK Sections 1(a) and 1(c) cover development/testing and organizational build servers | Tooling presence in restore assets does not grant permission to bundle SDK tools |
| BuildTools.MSIX 1.7.20250829.1 | Packaged SDK license and NOTICE retained for build tools | Check utility/redistributable lists if tools enter a distributable |
| DWrite 1.8.25122902 and Widgets 1.8.251231004 | Their Section 1(a) covers Windows development/testing | Section 3(a) omits the general binplaced-file grant found in the parent package. Parent/component applicability to DWriteCore.dll and Widgets payloads is **unresolved** |
| System.Numerics.Tensors 9.0.0 | MIT terms and third-party notices retained | Retain applicable notices in any distribution |

The [SDK license redirect](https://aka.ms/WinSDKLicenseURL) supplied by the BuildTools nuspec was retrieved as the [original Microsoft RTF](https://download.microsoft.com/download/0/F/F/0FF2B061-47DD-4F55-89B6-FD1D8C44F14D/sdk_license.rtf). Its unchanged source file is retained here, SHA256 `DD07EB178E00C6BBA4148457FC00FF77CD4887EB521D504186FE59C9EC8BBE62`. The redirect is not versioned; the retained artifact identifies the reviewed terms. The package terms and this source record were preserved from repository commit `8a1e8e411b6b0e8ff6758d71c14fb438014f7663`, without integrating the investigation branch.

The self-contained product output contains DWriteCore.dll and Widgets binaries even without a widget feature. Do not infer that unused features eliminate licensing obligations or silently replace component terms with parent terms. [Issue #13](https://github.com/fukuda-yuki/gh-projects-boards/issues/13) owns this unresolved distribution blocker after an explicitly approved scope transfer from #22. It must establish the applicable grant for the exact files, preserve evidence of the conclusion and verify the final notice manifest, including SDK-supplied .NET runtime packs, signing, updates and clean-machine behavior. The maintainer owns this verification; legal/vendor clarification may be needed.

These source notices are not automatically copied into the executable directory. No distributable, Release or binary publication is authorized by this development layout. See [Windows App SDK 1.8.11](https://github.com/microsoft/WindowsAppSDK/releases/tag/v1.8.11) for the pinned release; release notes do not override package licenses.
