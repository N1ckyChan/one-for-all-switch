# Third-party notices

## Deskflow

Thanks to the Deskflow developers and contributors for the keyboard and mouse sharing engine used by one-for-all-switch.

- Project: https://github.com/deskflow/deskflow
- Tested release: 1.26.0, commit `760e3b99b00053647a96b405276bf614bd860075`
- Source: https://github.com/deskflow/deskflow/tree/v1.26.0
- License: GPL-2.0-only; applicable source files include an OpenSSL linking exception. Copyright remains with the original authors.
- License copies: `licenses/Deskflow-GPL-2.0.txt` and `licenses/Deskflow-OpenSSL-exception.txt`.

Deskflow is launched as a separate executable. Its keyboard/mouse transport is not presented as original work of this project. This is an independent project, not an official Deskflow release.

The public one-for-all-switch application ZIP does not contain Deskflow, Qt or OpenSSL binaries. The optional `setup-deskflow.ps1` script obtains the unmodified Windows MSI from the official Deskflow release and preserves the licenses included in that package. Existing users can supply their own complete Deskflow directory. The tested official package reports Qt 6.10.1 and OpenSSL 3.6.1; these components retain their respective copyrights and licenses.

If you redistribute a locally assembled package containing Deskflow or its dependencies, preserve their notices and meet the corresponding source and other distribution requirements of each applicable license. A link to an upstream project alone does not replace those requirements.

## Microsoft .NET

The self-contained Windows application includes .NET 8 runtime components and Windows Forms. Their original license terms apply separately from this project's GPL-2.0-only license.

The runtime package licenses and third-party notices are included under `licenses/`:

- `dotnet-runtime-LICENSE.txt`
- `dotnet-runtime-THIRD-PARTY-NOTICES.txt`
- `dotnet-windowsdesktop-LICENSE.txt`

Sources: https://github.com/dotnet/runtime and https://github.com/dotnet/winforms.
