@echo off
setlocal
cd /d "%~dp0"

echo [1/2] 构建正式版（内嵌 requireAdministrator 清单）
dotnet build -c Release --nologo
if errorlevel 1 goto :fail

echo.
echo [2/2] 构建控制台测试变体（免提权，用于 --selftest / --serve-only）
dotnet build -c Release -t:Rebuild -p:OutputType=Exe -p:EmbedAdminManifest=false -o bin\test --nologo
if errorlevel 1 goto :fail

echo.
echo 完成：
echo   正式版  bin\Release\net48\LinLauncher.exe
echo   测试版  bin\test\LinLauncher.exe
exit /b 0

:fail
echo.
echo 构建失败
exit /b 1
