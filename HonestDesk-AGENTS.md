# AGENTS.md

## Purpose

HonestDesk is the Windows WPF administrative/support application for
HonestFlow. It includes support-request handling, tray/notification
behavior, RuDesktop launching, protected local settings,
client/configuration administration and license/device policy workflows.
It targets `net10.0-windows` and uses WPF (with Windows Forms enabled
where needed).

These instructions prioritize safe administrative behavior, preservation
of signing boundaries, and efficient Codex usage.

## Active branch context

Prepared against `agent/wpf-admin-workflow`. Preserve the checked-out
branch unless the user explicitly requests branch changes.

## Architecture map

Start here before broad search: - `HonestDesk/App.xaml` / `App.xaml.cs`
--- application resources/startup. - `HonestDesk/MainWindow*` and
tray/support UI --- main operator workflow. -
`HonestDesk/AdminAccessWindow*` --- protected administrative
entry/session. - `HonestDesk/AdminEditorWindow*` --- client/global
administration UI. - `HonestDesk/ClientProfileWindow*` --- read-only
client profile. - `HonestDesk/DeviceLicensePolicyWindow*` ---
device/license policy UI. - `HonestDesk/DiskCredentialsWindow*` ---
protected external-storage credentials UI. - `HonestFlow.Config.Core/`
--- shared configuration models and safe configuration reading. -
`HonestDesk/lib/HonestDesk.AdminRuntime.dll` --- referenced
administrative runtime dependency; do not
replace/reverse-engineer/reimplement it casually. - `README.md` ---
current operator/admin behavior and security expectations.

For a UI task, inspect the specific XAML + code-behind pair before
searching unrelated files.

## Security and licensing invariants

Do not change these without explicit user direction: 1. The private
ECDSA signing key belongs only to HonestDesk/its protected
administrative signing workflow. Never move it into HonestFlow or
HonestLicenseServer. 2. A newly produced license manifest/grant must be
locally signed, then verified with the public key before publication
according to the current workflow. 3. Published license revisions are
treated as immutable history; do not silently overwrite historical
signed revisions. 4. Do not expose passwords, tokens, private keys or
secret configuration values in read-only client cards, logs, dialogs,
exceptions, clipboard helpers, or telemetry. 5. Preserve Windows DPAPI
protection for local secrets/drafts unless explicitly redesigning secret
storage. 6. Preserve administrative session timeout and failed-login
lockout behavior unless explicitly requested otherwise. 7. Client/device
enable/disable and granular feature permissions are security decisions,
not cosmetic UI flags. 8. Never weaken signature verification or admin
authorization to make a workflow pass. 9. Never commit real IMAP
passwords, admin API keys, signing keys, tokens, Yandex Disk credentials
or generated protected state.

## HonestLicenseServer integration

HonestDesk is an administrative client of HonestLicenseServer. Do not
invent routes/contracts from memory. When changing server-backed
administration, inspect the current API client/runtime and server
contract expectations first.

Treat client creation/editing, devices, pending registration requests,
licenses, versions, features and offline policy as persistent server
state. Validate user intent before implementing destructive operations.

If a desired UI feature requires a new server endpoint or contract,
state that explicitly rather than faking it locally.

## Config.Core

`HonestFlow.Config.Core` is shared/safe configuration logic. Keep it
UI-independent. Do not introduce WPF-specific types into the core
library. Preserve safe/read-only behavior where the reader intentionally
excludes secrets. Changes here may affect multiple administrative views,
so inspect consumers before editing public models.

## WPF/UI rules

Use existing WPF patterns and resources. Keep XAML and code-behind
changes scoped to the owning window. Do not migrate the app to another
UI framework or introduce MVVM/framework rewrites as part of an
unrelated task. Avoid blocking the UI thread with IMAP, API, file,
signing, or remote-support operations. Preserve Russian user-facing
terminology used by surrounding screens.

Large `AdminEditorWindow.xaml.cs` changes should be especially focused:
prefer extracting logic only when it reduces concrete
duplication/complexity required by the task, not as speculative cleanup.

## External/support workflows

IMAP mail, RuDesktop launching, Yandex-related credentials and
filesystem state are external side effects. Do not contact live
services, send mail, connect to clients, launch remote sessions, or
mutate real remote state merely to validate code unless explicitly
requested.

## Agent efficiency rules

1.  Identify the target window/service first.
2.  Inspect only its direct dependencies and shared models needed for
    the task.
3.  Make the smallest coherent change.
4.  Do not run `dotnet restore`, `dotnet publish`, packaging, Git
    commit/push, release creation, or deployment unless explicitly
    requested.
5.  Do not launch the GUI or connect to IMAP/API/RuDesktop for ordinary
    code validation unless explicitly requested.
6.  Do not build after every edit.
7.  Small XAML/text/local code-behind change: no build by default.
8.  Cross-window/shared-model/admin-runtime integration change: one
    build is reasonable when it materially validates references/XAML
    compilation.
9.  On failure, fix only task-related errors; no repository-wide
    cleanup.

The user normally handles build/publish/Git operations separately.

## Validation

When validation is warranted, the normal solution build is:

``` powershell
dotnet build .\HonestDesk.slnx
```

Use static inspection for small changes. Never require real production
secrets or client connections for tests.

## Code style

Follow surrounding C#/XAML conventions. Avoid unrelated formatting.
Reuse existing resources, dialogs, services and models. Avoid adding
dependencies unless necessary. Keep secret-bearing values out of
exception/log messages.

## Cross-repository awareness

-   Backend/API: `1Yoda1/HonestLicenseServer`.
-   Client consuming licenses/configuration: `1Yoda1/HonestFlow`.

When changing license fields, device policy, versions or admin API
semantics, state which other repository is affected. Do not edit it
unless explicitly asked.

## Completion format

Report: what changed; files changed; whether build/validation ran;
API/license/security impact; exact manual validation step if useful.
Never commit, push, publish, deploy, send mail, connect remotely, or
publish a real license unless explicitly requested.
