# HonestDesk admin runtime

`HonestDesk.AdminRuntime.dll` contains the configuration and license administration
runtime embedded by HonestDesk. HonestDesk references this local binary and does not
depend on the external ConfigEditor project or its repository layout.

Rebuild the runtime from the reviewed ConfigEditor source before publishing a new
HonestDesk release.
