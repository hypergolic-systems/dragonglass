fn main() {
    #[cfg(target_os = "windows")]
    {
        let mut res = winresource::WindowsResource::new();
        res.set("FileVersion", "0.1.0.0");
        res.set("ProductVersion", "0.1.0.0");
        res.set("ProductName", "Dragonglass");
        res.set("FileDescription", "Dragonglass KSP native plugin");
        res.set("CompanyName", "Dragonglass");
        res.set("InternalName", "DgHudNative");
        res.set("OriginalFilename", "DgHudNative.dll");
        if let Err(e) = res.compile() {
            panic!("winresource compile failed: {e}");
        }
    }
}
