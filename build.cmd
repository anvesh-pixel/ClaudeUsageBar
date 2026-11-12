@echo off
rem Builds ClaudeUsageBar.exe with the C# compiler that ships with Windows (.NET Framework 4.x).
setlocal
set CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe
if not exist "%CSC%" set CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe
pushd "%~dp0"
"%CSC%" /nologo /target:winexe /optimize+ /platform:anycpu /out:ClaudeUsageBar.exe ^
  /r:System.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll ^
  ClaudeUsageBar.cs
set ERR=%ERRORLEVEL%
popd
exit /b %ERR%
