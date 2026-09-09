# Third-party notices for 一爪 Windows

The installed application also carries the upstream license and notice files
under its `Licenses` directory.

## NSIS 3.12 (graphical installer)

The standalone Windows Setup executable is built with NSIS 3.12. Its installer
engine, standard plug-ins, and zlib compressor are distributed under the
zlib/libpng license. The NSIS license and copyright notices are included with
the installer sources under `windows/installer/`; NSIS is not a separately
installed application on the user's computer. Source and license:
<https://nsis.sourceforge.io/> and
<https://nsis.sourceforge.io/Docs/AppendixI.html>.

## Microsoft Windows App SDK 1.8.260804001

一爪 uses the Microsoft Windows App SDK. The Microsoft software license
terms and upstream third-party notice are included as
`Licenses/Microsoft.WindowsAppSDK-LICENSE.txt` and
`Licenses/Microsoft.WindowsAppSDK-NOTICE.txt`. Package:
<https://www.nuget.org/packages/Microsoft.WindowsAppSDK/1.8.260804001>.

## Microsoft.ML.OnnxRuntime 1.24.4

一爪 uses ONNX Runtime for local Marian translation inference. ONNX Runtime
is distributed under the MIT License; its complete upstream license and
third-party notices are included in the installed `Licenses` directory.
Source: <https://github.com/microsoft/onnxruntime>. NuGet:
<https://www.nuget.org/packages/Microsoft.ML.OnnxRuntime/1.24.4>.

## Microsoft.ML.Tokenizers 2.0.0

一爪 uses Microsoft.ML.Tokenizers for local SentencePiece tokenization. Its
MIT license and complete upstream third-party notices are included in the
installed `Licenses` directory. Source:
<https://github.com/dotnet/machinelearning>. NuGet:
<https://www.nuget.org/packages/Microsoft.ML.Tokenizers/2.0.0>.

## Optional translation models

Translation model weights are not bundled in 一爪. 一爪 does bundle fixed,
hash-pinned copies of the two complete license texts; when a user explicitly
downloads a model, the matching text is injected into the local model pack
without a separate license-network request, and its license is shown in the interface. The pinned Chinese-to-English
model is CC BY 4.0; the pinned English-to-Chinese model is Apache 2.0. Exact
sources, revisions, hashes, and attribution rules are documented in
`src/Crosio.Windows.Translation/MODEL_PACK_FORMAT.md`.

## ScreenRecorderLib 7.0.0

一爪's Windows recording backend uses ScreenRecorderLib 7.0.0, copyright
Sverre Kristoffer Skodje and contributors. The package is distributed under the
MIT License. Source: <https://github.com/sskodje/ScreenRecorderLib>. NuGet:
<https://www.nuget.org/packages/ScreenRecorderLib/7.0.0>.

The following license text is reproduced from the package:

> MIT License
>
> Copyright (c) 2017 Sverre Skodje
>
> Permission is hereby granted, free of charge, to any person obtaining a copy
> of this software and associated documentation files (the "Software"), to deal
> in the Software without restriction, including without limitation the rights
> to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
> copies of the Software, and to permit persons to whom the Software is
> furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all
> copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
> IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
> FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
> AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
> LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
> OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
> SOFTWARE.
