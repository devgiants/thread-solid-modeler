ThreadSolidModeler
------------------

ThreadSolidModeler is a fork of the original coolOrange ThreadModeler by Philippe Leefsma.
This repository starts from that codebase and targets Autodesk Inventor 2026.

Description
-----------
ThreadSolidModeler is an Autodesk Inventor add-in that converts existing thread features into modeled 3D thread geometry.

It works on selected thread features in a Part document, opens a modeless dialog, lets you choose a thread sketch template, and generates the modeled result from the Inventor thread metadata.

The current default template is ISO Template.ipt. BSW Template.ipt is also shipped in the bundle and can be selected manually.

Usage
-----
1. Select one or more thread features in a Part document.
2. Launch ThreadSolidModeler from the Inventor ribbon.
3. Choose the template and pitch offset in the dialog.
4. Confirm to generate the modeled thread.

The original cosmetic thread feature is suppressed after the modeled geometry is created.

Installation
------------
The recommended installation path is to download the latest GitHub release, extract the ThreadSolidModeler.bundle folder, and copy it into:

%APPDATA%\Autodesk\ApplicationPlugins\

For an all-users installation, copy it to:

%ProgramData%\Autodesk\ApplicationPlugins\

Restart Autodesk Inventor 2026 after copying the bundle.

Author
------
Original code by Philippe Leefsma / coolOrange.
This fork is maintained as ThreadSolidModeler.
