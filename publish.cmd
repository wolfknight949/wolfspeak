@echo off
rem Double-click friendly wrapper: publishes both exe flavors (+ zips) into .\publish
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\publish.ps1" -All -Zip %*
if errorlevel 1 pause
