// Lists functions containing instructions whose mnemonic starts with any given prefix, with counts.
// Usage: -postScript FindMnem.java <outFile> <prefix> [prefix...]
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.*;
import java.util.*;
public class FindMnem extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        Map<Function, Integer> n = new HashMap<>();
        Listing l = currentProgram.getListing();
        for (Instruction ins : l.getInstructions(true)) {
            String m = ins.getMnemonicString();
            for (int i = 1; i < a.length; i++) if (m.startsWith(a[i])) {
                Function f = l.getFunctionContaining(ins.getAddress());
                if (f != null) n.merge(f, 1, Integer::sum);
                break;
            }
        }
        n.entrySet().stream().sorted((x, y) -> y.getValue() - x.getValue()).limit(60)
            .forEach(e -> out.println(e.getKey().getEntryPoint() + " " + e.getKey().getName() + " " + e.getValue()));
        out.close();
    }
}
