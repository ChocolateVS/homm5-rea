// Prints functions referencing each string given (one per line in the file named by the first script arg).
// Usage: -postScript StringXrefs.java <stringsFile> <outFile>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.data.*;
import ghidra.program.model.mem.*;
import java.io.*;
import java.nio.file.*;
import java.util.*;

public class StringXrefs extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        List<String> wanted = Files.readAllLines(Paths.get(args[0]));
        Set<String> want = new HashSet<>();
        for (String w : wanted) if (!w.isBlank()) want.add(w.trim());
        PrintWriter out = new PrintWriter(new FileWriter(args[1]));
        Listing listing = currentProgram.getListing();
        ReferenceManager rm = currentProgram.getReferenceManager();
        Memory mem = currentProgram.getMemory();
        for (String w : want) {
            byte[] pat = (w + "\0").getBytes("US-ASCII");
            Address a = mem.getMinAddress();
            int n = 0;
            while (a != null && n < 8) {
                a = mem.findBytes(a, pat, null, true, monitor);
                if (a == null) break;
                // Require a NUL (or non-printable) byte before, so we match whole strings.
                boolean whole = true;
                try { byte prev = mem.getByte(a.subtract(1)); whole = prev == 0; } catch (Exception e) {}
                if (whole) {
                    n++;
                    out.println("STRING " + w + " @ " + a);
                    for (Reference r : rm.getReferencesTo(a)) {
                        Address from = r.getFromAddress();
                        Function f = listing.getFunctionContaining(from);
                        out.println("  ref " + from + " in " + (f == null ? "<none>" : f.getName() + "@" + f.getEntryPoint()));
                    }
                }
                a = a.add(1);
            }
        }
        out.close();
    }
}
