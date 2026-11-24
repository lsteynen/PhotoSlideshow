# PhotoSlideshow

A lightweight Windows photo slideshow application.
Built in C# using Visual Studio 2026. Uses WPF and .NET Framework 4.7.

## Features

- Displays all images from the folder. Can search subfolders.
- Supports JPG, JPEG, PNG, BMP, and GIF files.
- Displays images in random order, with option to display in file discovery order.
- Animated transitions.
- A subtle zoom effect while the image is displayed.
- Reads image orientation metadata where available and decodes images near the display resolution.
- User can change interval time while running.
- User can pause and go back/forward while running.

## Requirements

- Windows 7 SP1 or higher
- .NET Framework 4.7 or later (standard with Windows 7 SP1 or higher)

## Usage

```text
PhotoSlideshow.exe [-f <folder>] [-s] [-nr] [-x] [-i <seconds>] [-m]
```

### Options

| Option | Description |
| --- | --- |
| `-f <folder>` | Folder containing images. Defaults to the application's executable folder. |
| `-s` | Include subfolders. |
| `-o` | Use discovered file order instead of random order. |
| `-x` | Reshuffle each time the slideshow cycles. Applies when random order is enabled. |
| `-m` | Start maximized in borderless full-screen mode. |
| `-i <seconds>` | Display each image for the specified positive whole number of seconds. Default: `8`. |
| `-h or -?` | Display usage help screen. |

### Examples

```text
PhotoSlideshow.exe
PhotoSlideshow.exe -f "C:\Users\Alex\Pictures"
PhotoSlideshow.exe -m -i 10
```

## Keyboard controls

| Key | Action |
| --- | --- |
| `Esc` or `Q` | Quit. |
| `Space` | Pause; press again to resume by advancing to the next image. |
| `Left` / `Right` | Show the previous / next image. |
| `Up` / `Down` | Increase / decrease the display interval by one second. |
| `F` or `F11` | Toggle full-screen and windowed mode. |
| `I` | Show slideshow information, including the version. |

