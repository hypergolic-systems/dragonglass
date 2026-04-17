fn main() {
    // Compile the Obj-C IOSurface blitter on macOS.
    #[cfg(target_os = "macos")]
    {
        println!("cargo:rerun-if-changed=src/iosurface_blitter.m");
        println!("cargo:rerun-if-changed=src/iosurface_blitter.h");
        cc::Build::new()
            .file("src/iosurface_blitter.m")
            .flag("-fobjc-arc")
            .flag("-fmodules")
            .compile("dg_iosurface_blitter");
        println!("cargo:rustc-link-lib=framework=Foundation");
        println!("cargo:rustc-link-lib=framework=Metal");
        println!("cargo:rustc-link-lib=framework=IOSurface");
        println!("cargo:rustc-link-lib=framework=CoreFoundation");
    }
}
