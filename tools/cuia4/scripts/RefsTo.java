// Lists every reference (code or data) to the given addresses, with the containing function or data label.
// Usage: -postScript RefsTo.java <outFile> <addr> [addr...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import java.io.*;

public class RefsTo extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(args[0]));
        Listing listing = currentProgram.getListing();
        ReferenceManager rm = currentProgram.getReferenceManager();
        for (int i = 1; i < args.length; i++) {
            Address a = toAddr(args[i].trim());
            out.println("REFS TO " + a);
            for (Reference r : rm.getReferencesTo(a)) {
                Address from = r.getFromAddress();
                Function f = listing.getFunctionContaining(from);
                out.println("  " + from + " " + r.getReferenceType() + " in " + (f == null ? "<data>" : f.getName() + "@" + f.getEntryPoint()));
            }
            // Raw pointer scan: 4-byte little-endian occurrences of the address (vtables without analysis refs).
            byte[] pat = new byte[] { (byte)a.getOffset(), (byte)(a.getOffset() >> 8), (byte)(a.getOffset() >> 16), (byte)(a.getOffset() >> 24) };
            Address p = currentProgram.getMinAddress();
            int n = 0;
            while (n < 16) {
                p = currentProgram.getMemory().findBytes(p, pat, null, true, monitor);
                if (p == null) break;
                out.println("  ptr @ " + p);
                p = p.add(1); n++;
            }
        }
        out.close();
    }
}
