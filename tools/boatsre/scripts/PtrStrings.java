// For each address, prints N dwords and the ASCII string each dword points to (if any).
// Usage: -postScript PtrStrings.java <outFile> <addr>:<count> ...
import ghidra.app.script.GhidraScript;
import ghidra.program.model.mem.*;
import ghidra.program.model.address.*;
import java.io.*;
public class PtrStrings extends GhidraScript {
    String str(Address a) {
        try {
            StringBuilder sb = new StringBuilder();
            for (int i = 0; i < 160; i++) { byte b = currentProgram.getMemory().getByte(a.add(i)); if (b == 0) break; if (b < 32 || b > 126) return null; sb.append((char) b); }
            return sb.length() >= 3 ? sb.toString() : null;
        } catch (Exception e) { return null; }
    }
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Memory m = currentProgram.getMemory();
        for (int i = 1; i < a.length; i++) {
            String[] p = a[i].split(":");
            Address base = toAddr(p[0]);
            int n = Integer.parseInt(p[1]);
            for (int k = 0; k < n; k++) {
                Address x = base.add(k * 4);
                int v = m.getInt(x);
                String s = null;
                try { s = str(toAddr(v & 0xffffffffL)); } catch (Exception e) {}
                out.printf("%s %08x %s%n", x, v, s == null ? "" : "\"" + s + "\"");
            }
            out.println();
        }
        out.close();
    }
}
