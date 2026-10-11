// glcheck: compile the vertex and fragment programs of one of this project's shaders (tools/shaderpack/*.glsl) with
// this Mac's own OpenGL, and print what the compiler says. A hand-packed shader that does not compile is drawn by the
// game in solid pink, and the game's log says nothing useful: check here first.
//
//     swiftc -O -o /tmp/glcheck tools/shaderpack/glcheck.swift && /tmp/glcheck tools/shaderpack/volume.glsl
//
// (The text of each program is what lies between "#ifdef VERTEX" or "#ifdef FRAGMENT" and the "#endif" that closes it,
// as make_bundle.py packs it.)
import AppKit
import OpenGL.GL3

func programs(_ text: String) -> [(String, String)] {
    var out: [(String, String)] = []
    for name in ["VERTEX", "FRAGMENT"] {
        guard let start = text.range(of: "#ifdef \(name)\n") else { continue }
        let rest = text[start.upperBound...]
        // the last "#endif" before the next "#ifdef" (or the end)
        let stop = rest.range(of: "\n#ifdef ")?.lowerBound ?? rest.endIndex
        let body = rest[..<stop]
        guard let end = body.range(of: "#endif", options: .backwards) else { continue }
        out.append((name, String(body[..<end.lowerBound])))
    }
    return out
}

func compile(_ name: String, _ source: String, _ kind: GLenum) -> Bool {
    let shader = glCreateShader(kind)
    source.withCString { pointer in
        var p: UnsafePointer<GLchar>? = pointer
        glShaderSource(shader, 1, &p, nil)
    }
    glCompileShader(shader)
    var ok: GLint = 0, length: GLint = 0
    glGetShaderiv(shader, GLenum(GL_COMPILE_STATUS), &ok)
    glGetShaderiv(shader, GLenum(GL_INFO_LOG_LENGTH), &length)
    if length > 1 {
        var log = [GLchar](repeating: 0, count: Int(length))
        glGetShaderInfoLog(shader, length, nil, &log)
        print("\(name):\n" + String(cString: log))
    }
    print("\(name): " + (ok != 0 ? "compiles" : "DOES NOT COMPILE"))
    return ok != 0
}

let arguments = CommandLine.arguments
guard arguments.count > 1, let text = try? String(contentsOfFile: arguments[1], encoding: .utf8) else {
    print("usage: glcheck file.glsl"); exit(2)
}
let attributes: [NSOpenGLPixelFormatAttribute] = [UInt32(NSOpenGLPFAOpenGLProfile), UInt32(NSOpenGLProfileVersion4_1Core), 0]
guard let format = NSOpenGLPixelFormat(attributes: attributes), let context = NSOpenGLContext(format: format, share: nil) else {
    print("no OpenGL context"); exit(1)
}
context.makeCurrentContext()
var all = true
for (name, source) in programs(text) {
    all = compile(name, source, name == "VERTEX" ? GLenum(GL_VERTEX_SHADER) : GLenum(GL_FRAGMENT_SHADER)) && all
}
exit(all ? 0 : 1)
