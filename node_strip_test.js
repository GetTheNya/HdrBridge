const HID = require('node-hid');
const dgram = require('dgram');

const TOTAL_LEDS = 65;
const UDP_PORT = 19446;
const VID = 0x1A86;
const PID = 0xFE07;

let device = null;
let currentSeq = 0x01;
let latestColors = null;
let isBusy = false;

// 1. Connection and initialization
function findAndConnectDevice() {
    const devices = HID.devices();
    const targetInfo = devices.find(d => d.vendorId === VID && d.productId === PID && d.path.toLowerCase().includes('mi_00'));
    
    if (!targetInfo) {
        console.error("❌ Can't find strip.");
        process.exit(1);
    }
    
    device = new HID.HID(targetInfo.path);
    console.log(`🔍 LED Strip found: ${targetInfo.product}`);

    // Unlocking
    const init1 = Buffer.alloc(65, 0); Buffer.from("52420602821e", "hex").copy(init1, 1);
    const init2 = Buffer.alloc(65, 0); Buffer.from("52420703930031", "hex").copy(init2, 1);
    device.write(init1);
    setTimeout(() => {
        device.write(init2);
        console.log("🔓 Initialization succsess!");
    }, 50);
}

// 2. Sending raw frame (without compression!)
function sendRawFrame(colorsArray) {
    if (!device || isBusy) return;
    isBusy = true;

    try {
        // Length: Header(2) + Len(2) + Seq(1) + (65 Leds * 5 bytes) + Commit(1) + Checksum(1) = 332 bytes
        const packetLen = 2 + 2 + 1 + (TOTAL_LEDS * 5) + 1 + 1; 
        const pkt = Buffer.alloc(packetLen, 0);

        pkt[0] = 0x53; pkt[1] = 0x43;            // Header SC
        pkt[2] = (packetLen >> 8) & 0xFF;        // Length (High) = 0x01
        pkt[3] = packetLen & 0xFF;               // Lengthg (Low)  = 0x4C
        pkt[4] = currentSeq;                     // Counter

        let offset = 5;
        for (let i = 0; i < TOTAL_LEDS; i++) {
            // Flag 0x80 is set ONLY for first led
            pkt[offset++] = (i === 0) ? (i | 0x80) : i; // Start
            pkt[offset++] = i;                          // End (the same led)
            pkt[offset++] = colorsArray[i].r;
            pkt[offset++] = colorsArray[i].g;
            pkt[offset++] = colorsArray[i].b;
        }

        pkt[offset++] = TOTAL_LEDS; // Frame application byte

        // Ideal checksum (simply the sum of all bytes)
        let sum = 0;
        for (let k = 0; k < offset; k++) sum += pkt[k];
        pkt[offset] = sum & 0xFF; 

        // Slicing 332 bytes into 64-byte chunks for USB
        for (let chunkStart = 0; chunkStart < packetLen; chunkStart += 64) {
            let usbReport = Buffer.alloc(65, 0);
            usbReport[0] = 0x00; // Report ID
            let copyLen = Math.min(64, packetLen - chunkStart);
            pkt.copy(usbReport, 1, chunkStart, chunkStart + copyLen);
            device.write(usbReport);
        }

        currentSeq = (currentSeq + 1) & 0xFF;
        if(currentSeq === 0) currentSeq = 1;

    } catch (e) {
        console.error("USB error:", e.message);
    }
    
    isBusy = false;
}

// 3. UDP Server and timer (30 FPS)
const server = dgram.createSocket('udp4');

server.on('message', (msg) => {
    let colors = [];
    for (let i = 0; i < msg.length; i += 3) {
        if (colors.length >= TOTAL_LEDS) break;
        colors.push({ r: msg[i], g: msg[i+1], b: msg[i+2] });
    }
    // If less data is received, fill the rest with black.
    while (colors.length < TOTAL_LEDS) colors.push({ r:0, g:0, b:0 });
    
    latestColors = colors;
});

setInterval(() => {
    if (latestColors) {
        sendRawFrame(latestColors);
        latestColors = null;
    }
}, 33);

server.bind(UDP_PORT);
findAndConnectDevice();