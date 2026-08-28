// Rewrites a PNG with its alpha channel removed, compositing over white.
//
// App Store Connect refuses an app icon that carries an alpha channel at all — opaque or not — and
// every rasteriser to hand emits RGBA. `sips` cannot drop the channel; going via JPEG would, but it
// would also put artefacts through a large flat gradient, which is most of this icon.
//
//     xcrun swift Mac/Tools/flatten-png.swift <in.png> <out.png>

import CoreGraphics
import Foundation
import ImageIO
import UniformTypeIdentifiers

let arguments = CommandLine.arguments
guard arguments.count == 3 else {
    FileHandle.standardError.write(Data("usage: flatten-png.swift <in.png> <out.png>\n".utf8))
    exit(2)
}

let input = URL(fileURLWithPath: arguments[1])
let output = URL(fileURLWithPath: arguments[2])

guard let source = CGImageSourceCreateWithURL(input as CFURL, nil),
      let image = CGImageSourceCreateImageAtIndex(source, 0, nil) else {
    FileHandle.standardError.write(Data("cannot read \(input.path)\n".utf8))
    exit(1)
}

// noneSkipLast is what actually drops the channel: the context has no alpha to write out, so the
// encoder emits RGB. White underneath, since anything transparent in the source should read as the
// paper it will sit on rather than as black.
guard let context = CGContext(
    data: nil,
    width: image.width,
    height: image.height,
    bitsPerComponent: 8,
    bytesPerRow: 0,
    space: CGColorSpace(name: CGColorSpace.sRGB)!,
    bitmapInfo: CGImageAlphaInfo.noneSkipLast.rawValue
) else {
    FileHandle.standardError.write(Data("cannot create bitmap context\n".utf8))
    exit(1)
}

let bounds = CGRect(x: 0, y: 0, width: image.width, height: image.height)
context.setFillColor(CGColor(red: 1, green: 1, blue: 1, alpha: 1))
context.fill(bounds)
context.draw(image, in: bounds)

guard let flattened = context.makeImage(),
      let destination = CGImageDestinationCreateWithURL(output as CFURL, UTType.png.identifier as CFString, 1, nil) else {
    FileHandle.standardError.write(Data("cannot write \(output.path)\n".utf8))
    exit(1)
}
CGImageDestinationAddImage(destination, flattened, nil)
guard CGImageDestinationFinalize(destination) else {
    FileHandle.standardError.write(Data("cannot finalise \(output.path)\n".utf8))
    exit(1)
}
