# Changelog

## 1.0.0 (2026-09-26)


### Features

* initial version ([f3e3009](https://github.com/zachthedev/winget-nudge/commit/f3e30093a6433c8011025e1b9e40c431e3e1710a))


### Bug Fixes

* **app:** bound crash.log by age and by size ([db7d887](https://github.com/zachthedev/winget-nudge/commit/db7d8874008e53550203b3f58e629a0a233f03ca))
* **app:** hand focus back and stop dimming rows in the picker ([8e36e25](https://github.com/zachthedev/winget-nudge/commit/8e36e259b871f0812079a437b241a27f6e01c1ab))
* **app:** open the settings page when settings.json cannot be read ([0b529cf](https://github.com/zachthedev/winget-nudge/commit/0b529cf95873609fa10a836c32db6d8ac9026fb0))
* **app:** report a mute the upgrade window could not save ([dbbf453](https://github.com/zachthedev/winget-nudge/commit/dbbf45341930beff0e6b78e41afd08fb1cfcd210))
* **app:** send the app's own version in the user agent ([4ef56bf](https://github.com/zachthedev/winget-nudge/commit/4ef56bf42ff383ef099eb1ca6d0b24718b232b67))
* **app:** show a scan that fails on a held state file as failed ([5a6ca71](https://github.com/zachthedev/winget-nudge/commit/5a6ca71a19f82765721a0d52b2a5139d96c4108f))
* **core:** carry a scan past a bookkeeping write that fails ([eefb14a](https://github.com/zachthedev/winget-nudge/commit/eefb14a365be65fca8c7e86b5f30c3f6a6372c93))
* **core:** decode every state file by its byte order mark ([47250ee](https://github.com/zachthedev/winget-nudge/commit/47250eed8bd6a512143cbfc83069584c1279d3fa))
* **core:** give a refused state file read the writers' wait ([623f3d0](https://github.com/zachthedev/winget-nudge/commit/623f3d07ef5333cf82b2c1f2306664572042255d))
* **core:** let a state file read open beside a rename ([f3e183c](https://github.com/zachthedev/winget-nudge/commit/f3e183cd5be833afb405065bb0dc5deed424286e))
* **core:** open state files relative to the verified directory ([94069d2](https://github.com/zachthedev/winget-nudge/commit/94069d236f4d55521457acb4b896d5c982323c7d))
* **core:** read version tracking through the state file read path ([ce0dc65](https://github.com/zachthedev/winget-nudge/commit/ce0dc654b0b73bda529611c205f4366fc814f24d))
* **core:** read winget's logs relative to a verified directory ([ed3f06f](https://github.com/zachthedev/winget-nudge/commit/ed3f06f335b9bc93348a317526b0c74a328ee6ad))
* **core:** record an upgrade whose bookkeeping write fails as upgraded ([4ceeb7d](https://github.com/zachthedev/winget-nudge/commit/4ceeb7de12d1f49a16086d79de4891a5d133f416))
* **core:** report winget progress on the reporting thread ([2af36cd](https://github.com/zachthedev/winget-nudge/commit/2af36cd3b542502fb9c3addfc2a0baa6f09f6676))
* **core:** retry a state file replace that another handle refuses ([b209ce8](https://github.com/zachthedev/winget-nudge/commit/b209ce8cf6744f473479923333fd860802f39b36))
* **core:** stop two processes dropping each other's state changes ([978034e](https://github.com/zachthedev/winget-nudge/commit/978034ecffae6592083943ec58c999a4e3dde2f7))
* **deps:** bump Microsoft.Data.Sqlite to 10.0.12 ([#11](https://github.com/zachthedev/winget-nudge/issues/11)) ([dfec36f](https://github.com/zachthedev/winget-nudge/commit/dfec36fb2f6e69c2e340748e6e03118fe8be27e1))
* **deps:** move to winget 1.29.380 ([2f08835](https://github.com/zachthedev/winget-nudge/commit/2f088355a7f03268d8be988e35d05a594f2a8e40))
* **installer:** refuse setup below Windows 11 ([20d9053](https://github.com/zachthedev/winget-nudge/commit/20d90537cef5939156415af053e5b99c8197f06b))
* **installer:** refuse setup when the Windows App Runtime is too old ([231abbb](https://github.com/zachthedev/winget-nudge/commit/231abbb62f8833ab55343a07e0455b9bc140aaf5))
* run one check and one upgrade at a time ([53396e1](https://github.com/zachthedev/winget-nudge/commit/53396e1b50bc65e79c7511d751049f61cf26ccbb))


### Performance

* drop the unused Windows ML runtime from the installer ([4fb26d9](https://github.com/zachthedev/winget-nudge/commit/4fb26d9134916f9c8285aa72d38142d28e0a44fe))
