// Reads typed values from program memory: <outFile> <addr>:<type>[:<count>] ...
// type: f=float32, d=float64, i=int32, u=uint32 (hex), s=C string
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.mem.*;
import java.io.*;

public class ReadData extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        Memory mem = currentProgram.getMemory();
        AddressSpace sp = currentProgram.getAddressFactory().getDefaultAddressSpace();
        for (int i = 1; i < args.length; i++) {
            String[] p = args[i].split(":");
            Address a = sp.getAddress(Long.parseLong(p[0], 16));
            String t = p.length > 1 ? p[1] : "f";
            int n = p.length > 2 ? Integer.parseInt(p[2]) : 1;
            StringBuilder sb = new StringBuilder(a + " " + t + ":");
            for (int k = 0; k < n; k++) {
                switch (t) {
                    case "f": sb.append(" ").append(Float.intBitsToFloat(mem.getInt(a.add(4L * k)))); break;
                    case "d": sb.append(" ").append(Double.longBitsToDouble(mem.getLong(a.add(8L * k)))); break;
                    case "i": sb.append(" ").append(mem.getInt(a.add(4L * k))); break;
                    case "u": sb.append(" 0x").append(Integer.toHexString(mem.getInt(a.add(4L * k)))); break;
                    case "s": {
                        StringBuilder s = new StringBuilder();
                        for (int j = 0; j < 256; j++) { byte b = mem.getByte(a.add(j)); if (b == 0) break; s.append((char) b); }
                        sb.append(" \"").append(s).append("\""); k = n; break;
                    }
                }
            }
            out.println(sb);
        }
        out.close();
    }
}
