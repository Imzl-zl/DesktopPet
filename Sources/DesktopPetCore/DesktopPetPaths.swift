import Foundation

/// Default on-disk locations: the app's data directory and its subfolders.
/// Kept in the core target so any layer can resolve paths without coupling
/// to the app bundle.
public enum DesktopPetPaths {
    public static var baseDir: String { NSHomeDirectory() + "/.desktoppet" }
    public static var petsDir: String { baseDir + "/pets" }
    public static var soundsDir: String { baseDir + "/sounds" }
}
