// Lists defined/undefined ASCII strings in memory matching a regex, with referencing functions.
// Usage: -postScript GrepStrings.java <regexFile> <outFile>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import ghidra.program.model.address.*;
import ghidra.program.model.symbol.*;
import ghidra.program.model.mem.*;
import java.io.*;
import java.util.regex.*;

public class GrepStrings extends GhidraScript {
    @Override
    public void run() throws Exception {
        String[] args = getScriptArgs();
        Pattern p = Pattern.compile(new String(java.nio.file.Files.readAllBytes(java.nio.file.Paths.get(args[0])), "UTF-8").trim());
        PrintWriter out = new PrintWriter(new FileWriter(args[1]));
        Memory mem = currentProgram.getMemory();
        ReferenceManager rm = currentProgram.getReferenceManager();
        Listing listing = currentProgram.getListing();
        for (MemoryBlock b : mem.getBlocks()) {
            if (!b.isInitialized() || b.isExecute()) continue;
            byte[] data = new byte[(int) b.getSize()];
            b.getBytes(b.getStart(), data);
            int start = -1;
            for (int i = 0; i <= data.length; i++) {
                boolean pr = i < data.length && data[i] >= 0x20 && data[i] < 0x7f;
                if (pr) { if (start < 0) start = i; continue; }
                if (start >= 0 && i - start >= 4) {
                    String s = new String(data, start, i - start, "US-ASCII");
                    if (p.matcher(s).find()) {
                        Address a = b.getStart().add(start);
                        StringBuilder sb = new StringBuilder();
                        int n = 0;
                        for (Reference r : rm.getReferencesTo(a)) {
                            if (n++ > 6) break;
                            Function f = listing.getFunctionContaining(r.getFromAddress());
                            sb.append(" ").append(f == null ? r.getFromAddress().toString() : f.getEntryPoint().toString());
                        }
                        out.println(a + " \"" + s + "\"" + sb);
                    }
                }
                start = -1;
            }
        }
        out.close();
    }
}
