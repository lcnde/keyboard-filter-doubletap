@echo off
rem Compila FiltroDoppioTap.exe con il compilatore C# incluso in Windows (.NET Framework 4.x)
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /codepage:65001 /target:winexe /optimize+ /out:FiltroDoppioTap.exe /r:System.Windows.Forms.dll /r:System.Drawing.dll FiltroDoppioTap.cs
if errorlevel 1 (
    echo.
    echo Compilazione fallita.
    exit /b 1
)
echo Compilato: FiltroDoppioTap.exe
