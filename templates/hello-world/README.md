# Hello World

最小可构建的 WXAML 项目。

```bash
dotnet /path/to/Warp.Cli.dll build --project /path/to/hello-world
dotnet /path/to/Warp.Cli.dll pack --project /path/to/hello-world
```

项目配置固定为根目录的 `manifest.yaml`。页面由 `src/pages/home/home.wxaml` 和同名 `.js` 文件组成。

默认会压缩生成代码中的方法名与局部变量名。调试时可在 `manifest.yaml` 的 `config` 中设置 `minifyIdentifiers: false`；该选项只影响构建，不会写入设备运行时 manifest。
