ThreadSolidModeler
------------------

ThreadSolidModeler is a fork of the original coolOrange ThreadModeler by Philippe Leefsma.
This repository starts from that codebase and targets Autodesk Inventor 2026.

Description
-----------
ThreadSolidModeler is an Autodesk Inventor add-in that converts existing thread features into modeled 3D thread geometry.

It works on selected thread features in a Part document, opens a modeless dialog, and generates the modeled result from the Inventor thread metadata.

The Thread Solid Modeler ribbon exposes two Part commands side by side:

- `Standard solid thread` for the ISO workflow.
- `3D print custom thread` for the dedicated print workflow.

The current default template is ISO Template.ipt. BSW Template.ipt is also shipped in the bundle and can be selected manually.

Usage
-----
1. Select one or more thread features in a Part document.
2. Launch `Standard solid thread` from the Inventor ribbon for the ISO workflow, or `3D print custom thread` for the print workflow.
3. The ISO dialog lets you choose the template and pitch offset.
4. The 3D Print dialog pre-fills a trapezoidal profile from the selected thread's nominal diameter and lets you override the values manually.
5. Confirm to generate the modeled thread.

The original cosmetic thread feature is suppressed after the modeled geometry is created.

Installation
------------
The recommended installation path is to download the latest GitHub release, extract the ThreadSolidModeler.bundle folder, and copy it into:

%APPDATA%\Autodesk\ApplicationPlugins\

For an all-users installation, copy it to:

%ProgramData%\Autodesk\ApplicationPlugins\

Restart Autodesk Inventor 2026 after copying the bundle.

The 3D Print path uses conservative profile defaults and validates the coil geometry before creating the feature to reduce coil failures.

Author
------
Original code by Philippe Leefsma / coolOrange.
This fork is maintained as ThreadSolidModeler.
