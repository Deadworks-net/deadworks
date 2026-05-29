use base64::Engine;

const MAX_BACKGROUND_BYTES: u64 = 25 * 1024 * 1024;

fn image_mime_type(path: &str) -> Result<&'static str, String> {
    let ext = std::path::Path::new(path)
        .extension()
        .and_then(|ext| ext.to_str())
        .map(|ext| ext.to_ascii_lowercase())
        .unwrap_or_default();

    match ext.as_str() {
        "png" => Ok("image/png"),
        "jpg" | "jpeg" => Ok("image/jpeg"),
        "webp" => Ok("image/webp"),
        "gif" => Ok("image/gif"),
        _ => Err("Background must be a PNG, JPG, WebP, or GIF image".into()),
    }
}

#[tauri::command]
pub fn validate_launcher_background(path: String) -> Result<(), String> {
    image_mime_type(&path)?;
    let metadata = std::fs::metadata(&path)
        .map_err(|e| format!("Failed to read background image metadata: {}", e))?;
    if !metadata.is_file() {
        return Err("Background path must point to a file".into());
    }
    if metadata.len() > MAX_BACKGROUND_BYTES {
        return Err(format!(
            "Background image is too large ({} bytes, max {} bytes)",
            metadata.len(), MAX_BACKGROUND_BYTES
        ));
    }
    Ok(())
}

#[tauri::command]
pub fn get_launcher_background_data_url(path: String) -> Result<String, String> {
    validate_launcher_background(path.clone())?;
    let mime_type = image_mime_type(&path)?;
    let bytes = std::fs::read(&path)
        .map_err(|e| format!("Failed to read background image: {}", e))?;
    let encoded = base64::engine::general_purpose::STANDARD.encode(bytes);
    Ok(format!("data:{};base64,{}", mime_type, encoded))
}
