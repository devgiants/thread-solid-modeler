# ThreadSolidModeler

[![Windows](https://img.shields.io/badge/Platform-Windows-lightgray.svg)](https://www.microsoft.com/en-us/windows/)
[![.NET Framework](https://img.shields.io/badge/.NET%20Framework-4.8-blue.svg)](https://dotnet.microsoft.com/)
[![Autodesk Inventor](https://img.shields.io/badge/Autodesk%20Inventor-2026-yellow.svg)](https://www.autodesk.com/products/inventor/)
[![Last release](https://img.shields.io/badge/last%20release-latest-blue.svg)](./releases/latest)

## Origin

ThreadSolidModeler is a fork of the original **coolOrange ThreadModeler** by **Philippe Leefsma**.
This repository starts from that codebase and continues it for Autodesk Inventor 2026.

## What it does

ThreadSolidModeler is an Autodesk Inventor add-in that turns existing thread features into modeled 3D thread geometry.

- It works on selected `ThreadFeature` objects in a Part document.
- It exposes a single command in the Inventor ribbon for the Part environment.
- It opens a modeless dialog where you choose the thread sketch template and the pitch offset.
- It uses the thread metadata already stored by Inventor to build the modeled result.
- It supports both standard and tapered threads.
- It suppresses the original cosmetic thread feature after the modeled geometry is created.

The current default template is `ISO Template.ipt`. `BSW Template.ipt` is still shipped in the bundle and can be selected manually.

## How it works

1. Select one or more thread features in a Part.
2. Launch `ThreadSolidModeler` from the ribbon.
3. The dialog loads the default template and lets you change it if needed.
4. The add-in reads the selected thread feature data from Inventor.
5. It builds a modeled 3D thread from the template sketch and the thread parameters.
6. The original cosmetic thread feature is suppressed once the modeled result is created.

The add-in is intentionally kept in the Part workflow only. The ribbon tab is shown for Part documents.

## Installation

### From a GitHub release

The release workflow publishes a zip archive for every tag that starts with `v`.

1. Download the latest release archive from GitHub.
2. Extract the `ThreadSolidModeler.bundle` folder from the zip.
3. Copy the bundle into one of these locations:
   - `%APPDATA%\Autodesk\ApplicationPlugins\` for the current Windows user
   - `%ProgramData%\Autodesk\ApplicationPlugins\` for all users on the machine
4. Restart Autodesk Inventor 2026.

The bundle contains the add-in manifest, the compiled assembly, the templates, and the supporting files Inventor expects at startup.

### From source

If you want to build it locally:

1. Open `ThreadModeler.sln`.
2. Build the `Release` configuration on Windows.
3. Use the generated `bin\release\ThreadSolidModeler.bundle` layout as the installable package.

## Notes

- The add-in targets Autodesk Inventor 2026.
- The release package is designed as a bundle, not as a standalone DLL drop.
- If you already have an older roaming bundle installed, remove it before testing a new release to avoid loading the wrong copy.

## Attribution

The original coolOrange / Philippe Leefsma attribution is preserved in the source headers and license file.
