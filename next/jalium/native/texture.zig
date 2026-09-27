pub export fn jalium_texture_convert(source: [*]const u8, target: [*]u8, pixels: usize, mode: u32) void {
    if (mode > 2) return;
    const length = pixels * 4;
    if (mode != 2) {
        @memcpy(target[0..length], source[0..length]);
        if (mode == 1) return;
        var offset: usize = 3;
        while (offset < length) : (offset += 4) {
            target[offset] = 255;
        }
        return;
    }

    var pixel: usize = 0;
    while (pixel < pixels) : (pixel += 1) {
        const offset = pixel * 4;
        const alpha = source[offset + 3];
        target[offset] = alpha;
        target[offset + 1] = alpha;
        target[offset + 2] = alpha;
        target[offset + 3] = 255;
    }
}
