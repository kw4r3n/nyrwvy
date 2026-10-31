#!/usr/bin/env python3
"""Regenerate/check csharp_facade_chars.bin with .NET 10 and the pinned Roslyn 5.0.0 package."""
from pathlib import Path
import os
import subprocess
import sys
import tempfile

root = Path(os.environ.get("NUGET_PACKAGES", Path.home() / ".nuget/packages"))
common = root / "microsoft.codeanalysis.common/5.0.0/lib/net9.0/Microsoft.CodeAnalysis.dll"
csharp = root / "microsoft.codeanalysis.csharp/5.0.0/lib/net9.0/Microsoft.CodeAnalysis.CSharp.dll"
target = Path(__file__).with_name("csharp_facade_chars.bin")
with tempfile.TemporaryDirectory() as directory:
    project = Path(directory)
    (project / "table.csproj").write_text(f'''<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><Reference Include="Microsoft.CodeAnalysis" HintPath="{common}"/><Reference Include="Microsoft.CodeAnalysis.CSharp" HintPath="{csharp}"/></ItemGroup></Project>''')
    (project / "Program.cs").write_text('''using System; using System.IO; using Microsoft.CodeAnalysis.CSharp;
var data = new byte[65536*3/8];
for (int i=0; i<65536; i++) {
    char c=(char)i;
    int v=(SyntaxFacts.IsIdentifierStartCharacter(c)?1:0) | (SyntaxFacts.IsIdentifierPartCharacter(c)?2:0) | (char.IsUpper(c)?4:0);
    int b=i*3;
    for (int j=0; j<3; j++) if ((v & (1<<j))!=0) data[(b+j)/8] |= (byte)(1<<((b+j)%8));
}
File.WriteAllBytes(args[0],data);
''')
    output = project / "table.bin"
    subprocess.run(["dotnet", "run", "--project", str(project / "table.csproj"), "--", str(output)], check=True)
    data = output.read_bytes()
    if sys.argv[1:] == ["--check"]:
        if data != target.read_bytes():
            raise SystemExit("character table differs")
        print("character table matches Roslyn 5.0.0 and .NET 10")
    elif not sys.argv[1:]:
        target.write_bytes(data)
    else:
        raise SystemExit("usage: csharp_facade_chars.py [--check]")
