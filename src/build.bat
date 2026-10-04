@echo off
rem Builds LaptopInputLock.exe with the C# compiler bundled with .NET Framework 4.x.
setlocal
cd /d "%~dp0"
"%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe" /nologo /optimize+ /target:winexe /platform:x64 /codepage:65001 /win32manifest:app.manifest /out:..\LaptopInputLock.exe LaptopInputLock.cs
exit /b %ERRORLEVEL%
