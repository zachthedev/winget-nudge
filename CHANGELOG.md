# Changelog

## 1.0.0 (2026-09-23)


### Features

* initial version ([358a6d0](https://github.com/zachthedev/winget-nudge/commit/358a6d0f9a54baef5a166f9e7747e5e2be6421bb))


### Bug Fixes

* **app:** hand focus back and stop dimming rows in the picker ([3593094](https://github.com/zachthedev/winget-nudge/commit/359309443b724a3dbf2bc8e5833283797f1c1fc0))
* **core:** report winget progress on the reporting thread ([f1f0d2d](https://github.com/zachthedev/winget-nudge/commit/f1f0d2d1298282add493fc92ea272521fde65436))
* **deps:** bump Microsoft.Data.Sqlite to 10.0.12 ([#11](https://github.com/zachthedev/winget-nudge/issues/11)) ([4c38d2f](https://github.com/zachthedev/winget-nudge/commit/4c38d2f16cc599eb05bc35a9ff4c39ffe4759d98))
* run one check and one upgrade at a time ([764dfff](https://github.com/zachthedev/winget-nudge/commit/764dffff0fc746aa9ad6ee4a09b60d3401096d8f))


### Build

* **deps:** bump prettier to exact 3.9.8 ([80c6d59](https://github.com/zachthedev/winget-nudge/commit/80c6d59a90731cb3393a6a00f21584755702399b))
* **deps:** move to winget 1.29.380 ([9db6ab3](https://github.com/zachthedev/winget-nudge/commit/9db6ab338acc9bb644c82dcaffc153d9eb03fd99))
* fail on a high or critical NuGet advisory and report the rest ([d021bff](https://github.com/zachthedev/winget-nudge/commit/d021bff7b868124668f4513720598734dd9c6128))
* install the workflow linters through mise and rework the gate ([7b2be97](https://github.com/zachthedev/winget-nudge/commit/7b2be97015f1206148314fe241f6a599210f2b56))
* report NuGet advisories, never block on them ([7761950](https://github.com/zachthedev/winget-nudge/commit/77619500dfac5403c61e492dc9f44213b3ff5d5c))
* **tools:** drop the checks the tools already make ([94e1161](https://github.com/zachthedev/winget-nudge/commit/94e11617aeeedf4e89c30373bece148e982be074))
* **tools:** read the mise files as TOML, pin mise on the action line ([de4be48](https://github.com/zachthedev/winget-nudge/commit/de4be487021432156f425f8e4982598f76caf8b7))
