const fs = require('fs');

function crc32(data) {
  let crc = 0xffffffff;
  const table = [];
  
  for (let i = 0; i < 256; i++) {
    let c = i;
    for (let j = 0; j < 8; j++) {
      c = (c & 1) ? (0xedb88320 ^ (c >>> 1)) : (c >>> 1);
    }
    table[i] = c;
  }
  
  for (let i = 0; i < data.length; i++) {
    crc = table[(crc ^ data[i]) & 0xff] ^ (crc >>> 8);
  }
  
  return (crc ^ 0xffffffff) >>> 0;
}

function createPNGChunk(type, data) {
  const length = Buffer.alloc(4);
  length.writeUInt32BE(data.length);
  
  const typeBuffer = Buffer.from(type, 'ascii');
  const crcData = Buffer.concat([typeBuffer, data]);
  const crcValue = crc32(crcData);
  
  const crc = Buffer.alloc(4);
  crc.writeUInt32BE(crcValue);
  
  return Buffer.concat([length, typeBuffer, data, crc]);
}

function createIconPNG(size) {
  const signature = Buffer.from([137, 80, 78, 71, 13, 10, 26, 10]);
  
  const ihdr = Buffer.alloc(13);
  ihdr.writeUInt32BE(size, 0);
  ihdr.writeUInt32BE(size, 4);
  ihdr[8] = 8;
  ihdr[9] = 6;
  ihdr[10] = 0;
  ihdr[11] = 0;
  ihdr[12] = 0;
  
  const rawData = [];
  for (let y = 0; y < size; y++) {
    rawData.push(0);
    for (let x = 0; x < size; x++) {
      const cx = size / 2;
      const cy = size / 2;
      const dist = Math.sqrt((x - cx) ** 2 + (y - cy) ** 2);
      const radius = size * 0.35;
      
      if (dist <= radius) {
        rawData.push(76, 175, 80, 255);
      } else {
        rawData.push(26, 26, 46, 255);
      }
    }
  }
  
  const rawBuffer = Buffer.from(rawData);
  
  const zlib = require('zlib');
  const compressed = zlib.deflateSync(rawBuffer);
  
  const iend = Buffer.alloc(0);
  
  const chunks = [
    createPNGChunk('IHDR', ihdr),
    createPNGChunk('IDAT', compressed),
    createPNGChunk('IEND', iend)
  ];
  
  return Buffer.concat([signature, ...chunks]);
}

const sizes = [16, 32, 48, 128];
const iconsDir = './icons';

if (!fs.existsSync(iconsDir)) {
  fs.mkdirSync(iconsDir, { recursive: true });
}

sizes.forEach(size => {
  const png = createIconPNG(size);
  fs.writeFileSync(`${iconsDir}/icon${size}.png`, png);
  console.log(`Created icon${size}.png`);
});

console.log('All icons generated successfully!');