# Microsoft Store package

This folder contains the Microsoft Store identity and visual assets for the
reserved `Fast Reader/Viewer` product.

```powershell
powershell -ExecutionPolicy Bypass -File .\packaging\Store\build-store-msix.ps1
```

The script publishes a self-contained Windows x64 build into a verified
temporary staging directory and writes the final package to:

```text
release\store\FastReaderViewer_<version>_x64.msix
```

Upload the `.msix` on the Partner Center **Packages** page. Microsoft Store
signs the package after certification. A locally installed or directly
distributed package needs a separate trusted signature.

The package identity is reserved in Partner Center as:

- Name: `gimkim.FastReaderViewer`
- Publisher: `CN=B725E616-C3B9-4EC5-9409-602191BCE92D`
- Publisher display name: `gimkim`

When running with package identity, the application leaves updates to
Microsoft Store and uses manifest-owned file associations instead of writing
its unpackaged registration to the registry.
