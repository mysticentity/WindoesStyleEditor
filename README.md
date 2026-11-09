# WindoesStyleEditor
A program that attempts to allow you full customization over windows UI elements.

How it is supposed to look like:

Tab 1: Visual Style Explorer (read-only, opens by default). It asks Windows to draw every class, part and state in the active visual style (Button, Menu, Tab, Scrollbar, Window caption and so on) and shows each as a tile. Classes are in a tree on the left, and each part gets its own section with its states laid out below it. It never modifies anything.

Tab 2: Style Editor & Exporter.

Opening a style: it opens any .msstyles file, or your current Windows style, and lists every embedded resource. Images are shown as before and after, with a transparency checkerboard.
Editing: you can change hue, saturation and lightness, or colorize to a picked color. A “keep neutral pixels” option leaves grays and whites alone. Edits apply to one image or to every PNG in the filtered list, or you can replace an image from a PNG of the exact same size.
Staging: everything is staged in memory only. Edited items turn orange in the tree, and the status bar always says “NOT applied”.
Exporting: you can export an edited copy of the .msstyles, or export all images as PNG files. The original is never modified.

Tab 3: Backups & Restore. It lists the backups, can create one manually, and restores one after a confirmation.

It is unfinished and I am uncertain If it will compile.
