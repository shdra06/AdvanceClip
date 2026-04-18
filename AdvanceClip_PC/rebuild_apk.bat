@echo off

cd ..\AdvanceClip_Android

echo Clearing previous builds and generating native code...
set "CI=true"
call npx expo prebuild --platform android --clean

echo.
echo Restoring SDK Routes...
cd android
echo sdk.dir=C\:\\Users\\Shivendra\\AppData\\Local\\Android\\Sdk>local.properties

echo.
echo Compiling Android APK natively...
call gradlew assembleRelease

echo.
echo Re-packaging...
cd ..
if not exist "..\AdvanceClip_PC\FINAL" mkdir "..\AdvanceClip_PC\FINAL"
copy /Y "android\app\build\outputs\apk\release\app-release.apk" "..\AdvanceClip_PC\FINAL\AdvanceClip_Mobile.apk" >nul

cd ..\AdvanceClip_PC
echo ==============================================
echo DONE! The updated APK is in 'AdvanceClip_PC\FINAL'.
echo ==============================================
