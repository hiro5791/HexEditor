# Plugins

This page shows how to add and manage plugins. A plugin is a small program that adds new features to HexEditor. How to build a plugin is described in the developer documentation, not in this manual.

A plugin can do anything on your PC with your rights. Add only plugins from authors you trust.

## Installing a plugin {#install}

Plugins are distributed as `.hexplugin` files.

1. Select **Tools > Plugins > Manage Plugins**. You can also run **Plugins: Manage** from the command palette.
2. Select **Add** and choose the `.hexplugin` file. You can also drop the file on the list.
3. A confirmation dialog appears. Read it, then select **Enable**. See [Plugin permissions and trust](#plugin-permissions) for details.
4. Select **Restart now**. The plugin is loaded after the restart.

If you have unsaved edits, you are asked about them before the restart, just as when you quit.

If you choose a broken file, or a file that is not a plugin, nothing is added and the reason is shown. If you add a newer version of a plugin you already have, it is treated as an update.

A plugin that does not fit your version of HexEditor (too new or too old) is not loaded, and the list shows the reason.

## Enabling, disabling and removing plugins {#manage}

The **Plugins** screen lists the plugins you have. Each plugin shows:

- The name, the version and the publisher
- The signature status (**Signed by**, **Not signed**, **Invalid signature**)
- The state: **Enabled**, **Disabled**, **Cannot be loaded** (with a reason) or **Stopped**
- A description, and the things the plugin adds (**Adds**)

You can:

| Action | What it does |
| --- | --- |
| The **Enabled** switch | Turns the plugin on or off |
| **Remove** | Removes the plugin |
| **Plugin settings** | Opens the settings page, if the plugin has one |
| **Open plugin folder** | Opens the folder that holds the plugins in File Explorer |
| **Restart now** | Restarts to apply the changes |

Turning a plugin on or off, removing it, and updating it take effect after you restart HexEditor. A plugin you changed shows "changes after a restart".

You can use the list with the keyboard alone.

### When something goes wrong {#when-things-go-wrong}

- If a plugin keeps causing errors, it is stopped for now and a notice appears at the top. You can choose **Disable the plugin** or **Try again at restart**. The error details stay in the **Output** panel, under **Plugin: name**.
- If HexEditor ended unexpectedly and a plugin seems to be the cause, you are asked at the next start, "Disable the plugin?". Choose **Disable** or **Keep enabled**.
- Things added by a plugin show "Added by plugin name" in their tooltip.

## Plugin permissions and trust {#plugin-permissions}

When you enable a plugin for the first time, or when its files have changed, a confirmation "Enable plugin name?" appears.

The dialog shows:

- The name, the version and the publisher
- A warning that "This code can do anything on your PC with your rights". A plugin does not run in a restricted environment like a script does.
- The list of permissions the plugin declares
- If the plugin is not signed, a warning that its publisher cannot be verified

The buttons are **Enable** and **Don't enable**. If you choose **Don't enable**, the plugin is recorded as disabled and you are not asked again at every start.

### Signatures and publishers {#signatures}

The executable files of a plugin can carry the digital signature of their publisher. HexEditor checks the signature and shows the status.

- **Signed by**: the name of the publisher is shown.
- **Not signed**: the plugin can still be loaded, but the confirmation dialog warns you more strongly.
- **Invalid signature**: something is wrong with the signature, for example the files were altered. This plugin is not loaded.

If you select **Always trust plugins from (name)** in the confirmation dialog, plugins signed by the same publisher are enabled without asking from then on. But if a file that the signature does not protect (such as a configuration file) changes, you are asked again.

Publishers you trust are listed under **Trusted publishers** on the **Plugins** screen. If you remove one, you are asked again next time.

If you do not want unsigned plugins to load at all, turn on **Load only signed plugins** in **Settings > Automation**. You need to restart for it to take effect.

### When running as administrator {#administrator}

When HexEditor runs as administrator, plugins also run with administrator rights. So at every start, the plugins to be loaded are shown in one list, and you choose **Load** or **Don't load**. If you choose **Don't load**, no plugin is loaded during that run.

### Safe mode {#safe-mode}

If a plugin or an automatic script keeps HexEditor from working properly, you can start it in safe mode.

- Start HexEditor with `--safe-mode` from a shortcut or the command line. See [Opening files from the command line](command-line.md#open-from-the-command-line).
- Or hold down the <kbd>Shift</kbd> key while you start HexEditor. Keep it down until the first window appears.

In safe mode, no plugins are loaded and no automatic scripts run. The window says "Started in safe mode. Plug-ins and automatically run scripts are not loaded." Scripts and macros that you run yourself still work.

In safe mode, the plugin list and the settings are not changed, and you cannot add plugins. To disable a problem plugin, start normally again and switch it off on the **Plugins** screen. **Restart normally** in the notice takes you back at once.

## What plugins can add {#what-plugins-add}

Plugins can add things like the following. What a plugin adds appears in the same place, and looks the same, as the built-in feature of that kind.

| What it adds | Where it appears |
| --- | --- |
| Inspector types | The data inspector ([Data inspector](inspector.md)) |
| Hashes | The hash screen ([Hashes](hashes.md)) |
| Import and export formats | The import and export screens ([Import and export](import-export.md)) |
| Data sources | **File > Open Plugin Data Source**, and **Open: name** in the command palette |
| Data operations | **Data > Plugin Operations** ([Data operations](data-operations.md)) |
| Commands | The command palette. You can also give them shortcuts |
| Menu items | **Tools > Plugins**, or **Plugins** in the right-click menu |
| Panels | A **Plugin panel** |
| Template functions | Functions you can call from [structure templates](templates.md) |
| File type detection | The [automatic selection](templates.md#auto-apply) of templates |
| Coloring rules | Coloring by value |
| Script APIs | Objects that scripts can use |

On the **Plugins** screen, **Adds** shows what each plugin provides.

How to build a plugin is described in the developer documentation (the SDK documentation). You can also read it on the [HexEditor plugin SDK](api-plugins.md) page. **Help > Scripting API reference** opens the page for the script API.
