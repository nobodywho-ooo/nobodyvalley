// Print the usable bounds (global points, top-left origin, below the menu bar) of a display:
//   swift scripts/screen-bounds.swift [display index]   ->  "x y width height"
// Index 0 is the main display; run with "list" to see all.
import AppKit

let screens = NSScreen.screens
let mainHeight = screens.first?.frame.height ?? 0

func bounds(_ s: NSScreen) -> (Int, Int, Int, Int) {
    // NSScreen uses a bottom-left origin; windows are positioned with a top-left origin.
    let f = s.visibleFrame
    return (Int(f.minX), Int(mainHeight - f.maxY), Int(f.width), Int(f.height))
}

let arg = CommandLine.arguments.dropFirst().first ?? "0"
if arg == "list" {
    for (i, s) in screens.enumerated() {
        let b = bounds(s)
        print("\(i): \(s.localizedName) \(b.0) \(b.1) \(b.2) \(b.3)")
    }
} else if let i = Int(arg), i < screens.count {
    let b = bounds(screens[i])
    print("\(b.0) \(b.1) \(b.2) \(b.3)")
} else {
    FileHandle.standardError.write("no display \(arg)\n".data(using: .utf8)!)
    exit(1)
}
