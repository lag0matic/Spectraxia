use std::{env, fs, path::PathBuf};

fn main() {
    println!("cargo:rerun-if-env-changed=TRUNKER_SOURCE");
    let root = PathBuf::from(env::var_os("TRUNKER_SOURCE").expect("Set TRUNKER_SOURCE to the trunker checkout"))
        .join("crates/trunker/src").canonicalize().expect("Cannot locate trunker sources");
    let mut modules = String::new();
    for (name, file) in [("dsp", "dsp/mod.rs"), ("p25", "p25/mod.rs"), ("pipeline", "pipeline.rs"), ("vocoder", "vocoder/mod.rs")] {
        let path = root.join(file);
        let watch = if name == "pipeline" { path.as_path() } else { path.parent().unwrap() };
        println!("cargo:rerun-if-changed={}", watch.display());
        modules.push_str(&format!("#[path={path:?}] mod {name};\n"));
    }
    fs::write(PathBuf::from(env::var_os("OUT_DIR").unwrap()).join("modules.rs"), modules).unwrap();
}
