# Describer After Dark

A Windows program, for adults 18 and over only, that describes sexy and explicit pictures for blind people. There are already good describers for safe-for-work images; this one is for the times you want the NSFW pictures described, fully and explicitly, with nothing censored and nothing made up.

Everything runs on your own computer with a local, uncensored vision model. No images are sent to any company.

On first launch it asks you to confirm you are 18 or older.

## Download

**[Download the latest version](../../releases/latest)**: get the `-win-x64.zip` file from the Releases page, extract it, and run `DescriberAfterDark.exe`. You don't need to build anything.

You need 64-bit Windows 10 or 11 and about 8 GB of free disk space. On first launch the program helps you install [Ollama](https://ollama.com), the free program that runs the description model, and then downloads the model (about 6 GB).

## How it works

1. Copy a new image every time; the shortcut describes whatever is on the clipboard right now.
   - **Luna for Reddit:** open a post with an image, press Shift+F10, and choose **Copy post image link**.
   - **Reddit for Blind or a browser:** copy the image link or the post link.
   - You can also copy an image itself, or an image file in File Explorer.
2. Press **Control Alt D** from anywhere.
3. The window comes up, the image is downloaded and described, and your screen reader speaks the description. The text is also in the Description box to review with the arrow keys.
4. Type a follow-up question in the question box and press Enter, for example "what color is her nail polish?" The answer is spoken and added to the end of the Description box.
5. If you press the shortcut without copying anything new, it tells you it's the same image as last time, even after restarting the program, and it warns you about a link that was already on the clipboard before it started; press it again within 10 seconds to describe it again anyway. Press F1 in the window to hear these instructions.
6. Press Escape to hide the window. The program keeps running in the system tray. Press Escape while it's working to cancel.

Keyboard shortcuts inside the window: Alt+D for the description, Alt+Q for the question box, Alt+A to ask, Alt+C to describe the clipboard, Alt+M to choose a model, F1 for instructions, Alt+H to hide.

## Choosing a model

Press **Alt M** in the main window (or choose **Choose model** from the tray icon's menu) to open the Models list. Each model in the list says its name, whether it's downloaded or in use, its size, and what it's good for. Press **Enter** on one to use it; if it isn't downloaded yet, it offers to download it first. **Alt D** downloads the selected model, **Alt R** removes one you don't use to free disk space, and **Alt A** adds any other Ollama model by name.

| Model | Size | Good for |
|---|---|---|
| Qwen3-VL 8B, uncensored (default) | 6.1 GB | Recommended; graphics cards with 8 GB or more |
| Qwen3-VL 4B, uncensored | 3.3 GB | Faster; 4 to 6 GB graphics cards or no graphics card |
| Qwen3-VL 2B, uncensored | 1.9 GB | Older or low-powered computers |
| Qwen3-VL 32B, uncensored | 21 GB | Most detailed; needs a 24 GB graphics card |
| Qwen 3.5 9B vision, uncensored | 6.6 GB | A different writing style, similar size to the default |
| Qwen2.5-VL 7B, uncensored | 6.0 GB | Previous generation; alternative to the default |
| Qwen2.5-VL 3B, uncensored | 3.2 GB | Small previous-generation model |
| Gemma 3 4B, uncensored | 8.6 GB | Google's Gemma; a different writing style |

## Updates

Describer After Dark updates itself. When a new version is published on GitHub, it asks whether you want to update; say yes and it downloads the new version, installs it, and restarts, with your settings kept. It checks when it starts and every few hours while running, and you can check any time with **Check for updates** in the tray icon's menu. If you say no, you'll be asked again next time it starts.

## Content rules

By default (`"DescriptionStyle": "Sensual"`), sexy and explicit pictures are described explicitly and vividly, including sex acts between any genders named by their common names, rather than clinically; ordinary images like memes get a normal friendly description. Set it to `"Plain"` for straightforward factual descriptions. In both styles the model is told to describe everything, including nudity, sexual content, and fetish content, with no warnings or softening. The one fixed rule, which can't be turned off: if anyone in an image appears to be under 18, the model replies only "I can't describe this image." That rule lives in `ImageDescriber.Core/PromptText.cs`.

## What you need

- Windows 10 or 11, 64-bit.
- [Ollama](https://ollama.com/download). If it isn't installed, the program offers to open the download page. If it's installed but not running, the program starts it.
- About 7 GB of free disk space for the model. On first launch the program offers to download it and announces progress every 10 percent.
- A graphics card with 8 GB or more of video memory makes descriptions take seconds. Without one it still works, just slower.

Default model: `huihui_ai/qwen3-vl-abliterated:8b-instruct`.

## Building from source

Most people should use the download above. To build it yourself:

First extract the whole zip (Extract All), then run `build.cmd` from the extracted folder. That's the only step. The window stays open at the end and says BUILD SUCCEEDED or BUILD FAILED; everything is also saved to `build-log.txt`.

- If the .NET 8 SDK isn't installed, `build.cmd` installs it with winget first. Windows may ask for permission.
- Prism (`prism.dll`, version 0.18.2, 64-bit) is already included in the `native` folder, along with its licenses. Nothing to download.
- The finished program, `DescriberAfterDark.exe`, is placed in the same folder as `build.cmd`, along with `prism.dll` and the `third-party-licenses` folder it needs. There is no separate publish folder.
- Exit Describer After Dark from its tray icon before rebuilding; `build.cmd` reminds you if it's still running.

For quick testing without publishing: `dotnet run --project ImageDescriber.App`

## Settings

Settings are in `%APPDATA%\DescriberAfterDark\settings.json`, created the first time the program runs. You can edit it in Notepad while the program is closed:

```json
{
  "Model": "huihui_ai/qwen3-vl-abliterated:8b-instruct",
  "OllamaUrl": "http://localhost:11434/",
  "HotkeyModifiers": "Control+Alt",
  "HotkeyKey": "D",
  "DescriptionStyle": "Sensual",
  "SpeakDescriptions": true,
  "MaxImageSide": 1600
}
```

Problems are written to `%APPDATA%\DescriberAfterDark\log.txt`, including which speech output was chosen (NVDA, JAWS, SAPI, or none).

## Publishing (for the maintainer)

To release an update to everyone:

1. Run `new-version.cmd` and type the new version number, such as 1.2.0. This is the only place the version is set.
2. Write what's new at the top of `RELEASE_NOTES.md`, under `## What's new in 1.2.0`. People see this text when they're asked whether to update.
3. Run `build.cmd`, then `publish-to-github.cmd`.

Everyone running the program is then offered the update automatically. `make-release.cmd` (used by the publish script) packs the built program into `release\DescriberAfterDark-<version>-win-x64.zip`; the version always comes from the built program, so the release and the program can't disagree.

## License

The program is released under the MIT License (see `LICENSE`). Prism, used for screen reader speech, is under the Mozilla Public License 2.0 (see `native/licenses`).

## Project layout

- `ImageDescriber.Core`: everything that isn't Windows UI. Downloading images (direct links, Reddit media viewer links, and pages wrapping an image), talking to Ollama, the conversation history, the prompt, settings, and Prism speech.
- `ImageDescriber.App`: the Windows Forms window, tray icon, global hotkey, clipboard reading, and image resizing.

## Known limitations

- No installer yet. Ollama is installed manually from the download page the program opens.
- Reddit post links work (the post's own image is used); for galleries only the first image is described for now.
- No settings window; edit `settings.json` by hand.
- JoyCaption support isn't wired in yet.
- WebP images are sent to Ollama as-is, since Windows' built-in image library can't convert them. Most Reddit images are JPEG or PNG.
