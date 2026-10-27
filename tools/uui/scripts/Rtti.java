// For each vtable prints its RTTI class name and the TypeDescriptor address, plus all refs to the TD.
// Usage: Rtti.java <outFile> <vt> [vt...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.address.*;
import ghidra.program.model.listing.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class Rtti extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        for (int i = 1; i < a.length; i++) {
            Address vt = toAddr(a[i]);
            long col = getInt(vt.subtract(4)) & 0xffffffffL;
            long td = getInt(toAddr(col + 12)) & 0xffffffffL;
            StringBuilder sb = new StringBuilder();
            Address p = toAddr(td + 8);
            for (int k = 0; k < 200; k++) { byte b = getByte(p.add(k)); if (b == 0) break; sb.append((char) b); }
            out.printf("VT %s COL %08x TD %08x %s%n", vt, col, td, sb);
            for (Reference r : getReferencesTo(toAddr(td))) {
                Function f = getFunctionContaining(r.getFromAddress());
                out.println("   ref " + r.getFromAddress() + " " + (f == null ? "" : f.getName() + "@" + f.getEntryPoint()));
            }
        }
        out.close();
    }
}
