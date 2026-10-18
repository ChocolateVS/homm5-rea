// Prints N little-endian dwords at each address (e.g. vtable slots) with the function they point into.
// Usage: -postScript DumpDwords.java <outFile> <count> <addr> [addr...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import java.io.*;

public class DumpDwords extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        int n = Integer.parseInt(a[1]);
        for (int i = 2; i < a.length; i++) {
            Address base = toAddr(a[i]);
            out.println("AT " + base);
            for (int k = 0; k < n; k++) {
                long v = getInt(base.add(4L * k)) & 0xffffffffL;
                Function f = getFunctionContaining(toAddr(v));
                out.printf("  [%d] %08x %s%n", k, v, f == null ? "" : f.getName() + "@" + f.getEntryPoint());
            }
        }
        out.close();
    }
}
