@echo off
rem SPDX-License-Identifier: GPL-2.0-or-later
setlocal
"%~dp0SQMPluginInstaller.exe" uninstall
if errorlevel 1 (
  echo Uninstallation failed.
  pause
  exit /b 1
)
pause
