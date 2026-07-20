@echo off
setlocal
dotnet run --project "%~dp0src\IoFtp.Desktop\IoFtp.Desktop.csproj"
if errorlevel 1 pause
endlocal
