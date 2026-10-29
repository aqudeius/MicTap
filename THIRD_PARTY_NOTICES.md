# Third-party notices

MicTap uses [NAudio 2.2.1](https://www.nuget.org/packages/NAudio/2.2.1), an audio library for .NET by Mark Heath and contributors, under the MIT license.

- Project: <https://github.com/naudio/NAudio>
- Copyright notice supplied with this version: Copyright 2020 Mark Heath
- Full license text: [licenses/NAudio-LICENSE.txt](licenses/NAudio-LICENSE.txt)

NAudio's component packages are NAudio.Asio, NAudio.Core, NAudio.Midi, NAudio.Wasapi, NAudio.WinForms, and NAudio.WinMM, all version 2.2.1 and licensed under MIT.

The resolved NuGet dependency graph also includes these Microsoft packages under MIT:

| Package | Version |
| --- | --- |
| Microsoft.NETCore.Platforms | 3.1.0 |
| Microsoft.Win32.Registry | 4.7.0 |
| System.Security.AccessControl | 4.7.0 |
| System.Security.Principal.Windows | 4.7.0 |

Their package metadata identifies Microsoft Corporation. Their supplied license text credits the .NET Foundation and Contributors. Retained upstream files are [licenses/dotnet-LICENSE.txt](licenses/dotnet-LICENSE.txt) and [licenses/dotnet-THIRD-PARTY-NOTICES.txt](licenses/dotnet-THIRD-PARTY-NOTICES.txt). These upstream notices are retained as supplied and cover components beyond those used by MicTap. Project: <https://github.com/dotnet/corefx>.

Dependencies are restored through NuGet. Their source and compiled binaries are not vendored in this repository. The project copies MicTap's license, these notices, and the retained dependency licenses into build and publish output; retain them when distributing the application.

Third-party virtual audio cable drivers are installed separately by users. They are not included in MicTap and remain subject to their respective licenses. The shelved custom-driver project is not part of this repository.
