@echo off
color 0a
echo ==============================================
echo AdvanceClip - Standalone Compiler Pipeline
echo ==============================================

if not exist "FINAL" mkdir "FINAL"

echo.
echo [1/3] Purging heavy uncompiled local Runtime Caches...
cmd /c "rmdir /S /Q "Scripts\CloudUploader\node_modules" "Scripts\CloudUploader\cloud-profile" "Scripts\CloudUploader\browser_cache"" >nul 2>nul
FOR /d /r . %%d in (__pycache__) DO @if exist "%%d" rd /s /q "%%d" >nul 2>nul

echo.
echo [2/3] Compiling Desktop C# Executable (Single-File Native Win64)...
dotnet publish AdvanceClip.csproj -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:PublishReadyToRun=true -p:IncludeNativeLibrariesForSelfExtract=true -p:IncludeAllContentForSelfExtract=true -o "FINAL"

:: Aggressive cleanup: Nuke any lingering unzipped C++ libraries or collateral output to guarantee 100% single-file purity.
del /Q "FINAL\*.pdb" "FINAL\*.config" "FINAL\*.dll" "FINAL\*.json" >nul 2>nul

echo.
echo [3/3] Initiating Android APK Rebuild pipeline natively...
call rebuild_apk.bat

echo.
echo ==============================================
echo [SUCCESS] Standalone Compilation Complete!
echo You can find the pure standalone "AdvanceClip.exe"
echo and "AdvanceClip_Mobile.apk" sitting cleanly inside 
echo the 'FINAL' directory.
echo ==============================================
