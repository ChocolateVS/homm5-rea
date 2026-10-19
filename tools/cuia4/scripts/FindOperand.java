// Lists instructions in [start,end) whose text matches a regex. Usage: <out> <start> <end> <regex>
import ghidra.app.script.GhidraScript;
import ghidra.program.model.listing.*;
import java.io.*;
public class FindOperand extends GhidraScript {
    @Override public void run() throws Exception {
        String[] a = getScriptArgs();
        PrintWriter out = new PrintWriter(new FileWriter(a[0]));
        java.util.regex.Pattern p = java.util.regex.Pattern.compile(a[3]);
        InstructionIterator it = currentProgram.getListing().getInstructions(toAddr(a[1]), true);
        long end = toAddr(a[2]).getOffset();
        while (it.hasNext()) {
            Instruction i = it.next();
            if (i.getAddress().getOffset() >= end) break;
            String s = i.toString();
            if (p.matcher(s).find()) {
                Function f = getFunctionContaining(i.getAddress());
                out.println(i.getAddress() + " " + s + "  in " + (f == null ? "?" : f.getName()));
            }
        }
        out.close();
    }
}
