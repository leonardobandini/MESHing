@echo off
rem SPDX-License-Identifier: GPL-2.0-or-later
setlocal
if /I "%~1"=="verify" (
  "%~dp0SQMPluginInstaller.exe" verify
  exit /b
)
if not exist "%~dp0MESHingSetup.exe" (
  echo MESHing graphical installer is missing.
  pause
  exit /b 1
)
start "" "%~dp0MESHingSetup.exe"
