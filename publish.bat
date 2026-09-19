@echo off
setlocal
set ROOT=%~dp0
set OUT=%ROOT%publish_v0915

echo === publish DiskEye (green portable folder, dual-role: --etw-child) ===
dotnet publish "%ROOT%src\DiskEye\DiskEye.csproj" -c Release -o "%OUT%"
if errorlevel 1 goto :fail

echo.
echo === OK: %OUT% ===
echo Deliverable: entire publish_v0915 folder (copy anywhere, run DiskEye.exe)
endlocal
exit /b 0

:fail
echo.
echo !!! publish failed
endlocal
exit /b 1
