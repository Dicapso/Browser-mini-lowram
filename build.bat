@echo off
:: Browser Mini - Windows-un daxili C# kompilyatoru il? y???l?r (he? n? qura?d?rmaq laz?m deyil)
cd /d "%~dp0"
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /r:System.Web.Extensions.dll /r:System.Security.dll /r:System.Windows.Forms.dll /r:System.Drawing.dll /out:BrowserMini.exe BrowserMini.cs
if %errorlevel%==0 (echo Hazirdir: BrowserMini.exe) else (echo Xeta bas verdi)