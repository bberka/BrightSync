export const REPO = 'bberka/BrightSync';
export const REPO_URL = `https://github.com/${REPO}`;
export const RELEASES_URL = `${REPO_URL}/releases/latest`;
export const ISSUES_URL = `${REPO_URL}/issues`;
export const SITE_TITLE = 'BrightSync';
export const SITE_TAGLINE = 'One brightness for every monitor';
export const SITE_DESCRIPTION =
  'BrightSync is a free, open-source tray app for Windows and Linux that keeps all your monitors, including the laptop panel, at one shared brightness. DDC/CI, automatic curve, idle dimming, eye protection and a CLI.';

/** Joins a path onto the configured base so links work on the GitHub Pages project URL. */
export function url(path = ''): string {
  const base = import.meta.env.BASE_URL.replace(/\/$/, '');
  return `${base}/${path.replace(/^\//, '')}`;
}

export interface Feature {
  title: string;
  body: string;
  icon: string;
}

export const features: Feature[] = [
  {
    icon: 'sun',
    title: 'One master slider',
    body: 'Move one slider in the tray and every enabled monitor follows, laptop panel included. Set limits once and forget them.',
  },
  {
    icon: 'monitor',
    title: 'DDC/CI for external monitors',
    body: 'Talks to the monitor itself, so it works with the hardware controls instead of a color overlay. Contrast, volume, RGB gain, presets and input source are one click away where the monitor allows.',
  },
  {
    icon: 'laptop',
    title: 'Laptop panels too',
    body: 'Built-in displays are controlled through WMI on Windows and the backlight interface on Linux, and treated like any other monitor.',
  },
  {
    icon: 'curve',
    title: 'Automatic 24-hour curve',
    body: 'Drag points on a smooth curve and brightness follows the clock, recalculating after sleep and time changes.',
  },
  {
    icon: 'moon',
    title: 'Idle dimming and power saving',
    body: 'Dim after inactivity, optionally ignoring media playback, and reduce brightness when the system power saver turns on.',
  },
  {
    icon: 'eye',
    title: 'Eye protection and boost',
    body: 'One-click timed dimming for late evenings, or a timed boost for bright rooms. The two modes never fight each other.',
  },
  {
    icon: 'sliders',
    title: 'Per-monitor calibration',
    body: 'Minimum, maximum and a multiplier per monitor so mismatched panels look the same at every level.',
  },
  {
    icon: 'refresh',
    title: 'Survives sleep and unplugging',
    body: 'Re-applies brightness after wake, unlock and hot-plug, and can recover monitors that forget their settings.',
  },
  {
    icon: 'terminal',
    title: 'Scriptable CLI',
    body: 'Bind your own hotkeys with brightness up/down, status --json and more. No hotkey daemon needed.',
  },
  {
    icon: 'github',
    title: 'Open source, no telemetry',
    body: 'MIT licensed, built in the open. The only network request is the optional update check against GitHub.',
  },
  {
    icon: 'monitor',
    title: 'Windows and Linux',
    body: 'The same app and settings file on both. Packages for Debian, Fedora, Arch, Alpine, openSUSE, AppImage and Windows, on x64, arm64 and more.',
  },
  {
    icon: 'bolt',
    title: 'Tiny and fast',
    body: 'Compiled with Native AOT: a single self-contained binary, instant startup and a small memory footprint.',
  },
];

export interface Faq {
  question: string;
  answer: string;
}

export const faqs: Faq[] = [
  {
    question: 'Will it work with my monitor?',
    answer:
      'External monitors need DDC/CI, which almost every monitor with a DisplayPort or HDMI input supports and which can be switched on in the monitor’s own menu. Docks, KVMs and some adapters block it. Each monitor row in Settings tells you which backend was used and why a monitor is not controllable.',
  },
  {
    question: 'Does it work on Wayland?',
    answer:
      'Yes. BrightSync runs through XWayland, and brightness control does not depend on the display server because it talks to the monitor over I²C and to the laptop panel through the kernel.',
  },
  {
    question: 'Why do I not see the tray icon on GNOME?',
    answer:
      'GNOME does not show tray icons by default. Install the AppIndicator and KStatusNotifierItem extension, or control BrightSync from the command line. KDE, XFCE, Cinnamon, MATE and others work out of the box.',
  },
  {
    question: 'Which Linux distributions are supported?',
    answer:
      'Packages are provided for Debian and Ubuntu (deb), Fedora and openSUSE (rpm), Arch (pacman), Alpine (apk), an AppImage for anything else, and a portable archive for architectures such as riscv64. The test suite and a launch test run on Ubuntu, Debian, Fedora, Alpine, Arch and openSUSE.',
  },
  {
    question: 'How does it update?',
    answer:
      'On Windows BrightSync checks GitHub releases and can download, verify the SHA-256 checksum and install the matching installer. On Linux it tells you a new version exists and you update through your package manager or by downloading the new package.',
  },
  {
    question: 'Is it free? Does it collect data?',
    answer:
      'It is free and open source under the MIT license. The only network request is the optional update check against GitHub. Diagnostics export is a local file you choose to share.',
  },
];
