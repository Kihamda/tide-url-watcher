# Tide

Tide is a calm desktop reader for the websites you care about. Register a web
page or feed URL and Tide collects new stories into one focused inbox.

The desktop app is built with React Native for Windows and macOS. It does not
embed a browser engine or use Tauri.

## Features

- RSS and Atom parsing with feed autodiscovery
- HTML article-card fallback for sites without feeds
- Unread, saved, source, and text filters
- Local-only storage through AsyncStorage
- Responsive three-panel desktop UI
- Shared TypeScript UI and reader logic across Windows and macOS

## Stack

| Layer | Choice |
| --- | --- |
| UI | React Native 0.81.6 |
| Windows | React Native Windows 0.81.25, Fabric C++ app |
| macOS | React Native macOS 0.81.7 |
| Persistence | `@react-native-async-storage/async-storage` |
| Feed parsing | `fast-xml-parser` |

The React Native minor version is intentionally pinned to `0.81`: the current
macOS package requires `react-native 0.81.6`, while Windows supports the same
minor. Keeping that common baseline avoids platform forks.

## Develop

Install dependencies:

```powershell
npm install
```

Run Metro:

```powershell
npm start
```

Run Windows from a second terminal:

```powershell
npm run windows
```

Run macOS on a Mac:

```bash
cd macos && pod install && cd ..
npm run macos
```

Windows development requires Visual Studio 2022 with the React Native Windows
workload. macOS development requires Xcode and CocoaPods.

## Validate

```powershell
npm run typecheck
npm run lint
npm run test:ci
npm audit --omit=dev
```

The repository also bundles JavaScript for both desktop platforms in CI before
running native platform builds.

## Release

Push a semantic version tag such as `v0.1.0`. GitHub Actions builds an unsigned
Windows x64 ZIP and an unsigned macOS app ZIP, then publishes both files in a
GitHub Release. Code-signing secrets can be added before a public store release.

## Architecture Notes

The initial version keeps feed parsing in TypeScript to stay small and avoid an
OS-specific bridge. A Rust core remains a reasonable later optimization if
large subscription sets make parsing measurable. At that point, expose one
small Turbo Native Module contract on both platforms and keep the React Native
UI unchanged.
