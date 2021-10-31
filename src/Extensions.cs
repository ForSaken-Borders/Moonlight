namespace Moonlight
{
    public static class Extensions
    {
        public static int GetVarIntLength(this int val)
        {
            int amount = 0;
            do
            {
                val >>= 7;
                amount++;
            } while (val != 0);

            return amount;
        }
    }
}