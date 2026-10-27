@echo off
rem Budowanie bez Visual Studio — wystarczy .NET SDK (https://dotnet.microsoft.com/download)
rem Wynik: out\AppInstaller.exe (jeden plik, ~0,5 MB)
setlocal
cd /d "%~dp0"
if not exist keys\public.xml (
  echo [!] Brak keys\public.xml - exe zadziala tylko z katalogiem wbudowanym, bez aktualizacji.
  echo     Uruchom: powershell -ExecutionPolicy Bypass -File tools\New-SigningKey.ps1
)
dotnet build src\AppInstaller\AppInstaller.csproj -c Release -o out %*
if errorlevel 1 exit /b 1
for %%F in (out\AppInstaller.exe) do echo. & echo Gotowe: %%~fF (%%~zF B)
