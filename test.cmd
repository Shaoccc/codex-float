@echo off
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist .build mkdir .build
"%CSC%" /nologo /target:exe /main:ParserTests /codepage:936 /out:.build\QuotaTests.exe /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Web.Extensions.dll CodexFloat.cs Tests.cs
if errorlevel 1 exit /b 1
.build\QuotaTests.exe
exit /b %errorlevel%
