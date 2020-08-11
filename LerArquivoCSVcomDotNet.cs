// Como o GC impacta a performance em .NET: Fazendo "parsing" de arquivos grandes

// Realizar o parse de arquivos grandes é uma tarefa recorrente no dia a dia de desenvolvedores, ao mesmo tempo em que pode ser desafiadora. É muito fácil, ao fazer parsing, escrever código lento que consome muita memória. Como exemplo, vamos considerar um arquivo CSV com a seguinte estrutura (o tamanho completo do arquivo é de ~ 500 MB)
// https://grouplens.org/datasets/movielens/

userId,movieId,rating,timestamp
1,2,3.5,1112486027
1,29,3.5,1112484676
1,32,3.5,1112484819
1,47,3.5,1112484727
1,50,3.5,1112484580
1,112,3.5,1094785740
1,151,4.0,1094785734
1,223,4.0,1112485573
1,253,4.0,1112484940

// Vamos supor que você precise calcular a média das classificações, ou ratings, dadas ao filme ‘Coração Valente’, que possui `movieId`= 110. Como você o implementaria? Provavelmente uma primeira versão seria semelhante com a seguinte implementação:

var lines = File.ReadAllLines(filePath);
var sum = 0d;
var count = 0;

foreach (var line in lines)
{
    var parts = line.Split(',');

    if (parts[1] == "110")
    {
        sum += double.Parse(parts[2], CultureInfo.InvariantCulture);
        count++;
    }
}

Console.WriteLine($"Classificação média para o filme Coração Valente é {sum/count} ({count}).");

// O código anterior é fácil de ler, o que é muito bom. Entretanto, é lento (demorou mais de 6 segundos para ser executado na minha máquina) e consome muita RAM (mais de 2 GB alocados processando um arquivo de 500 MB).

// O principal problema dessa implementação é que estamos trazendo todos os dados para a memória, pressionando bastante o garbage collector. Não há necessidade de fazer isso.

var sum = 0d;
var count = 0;
string line;

using (var fs = File.OpenRead(filePath))
using (var reader = new StreamReader(fs))
while ((line = reader.ReadLine()) != null)
{
    var parts = line.Split(',');

    if (parts[1] == "110")
    {
        sum += double.Parse(parts[2], CultureInfo.InvariantCulture);
        count++;
    }
}
Console.WriteLine($"Classificação média para o filme Coração Valente é {sum/count} ({count}).");

// Desta vez, estamos carregando dados conforme necessário e descartando-os da memória. Esse código é ~ 30% mais rápido que o anterior, exige menos memória (não mais que 13 MB para processar um arquivo de 500 MB) e exerce menos pressão sobre o garbage collector (não há mais grandes objetos nem objetos que sobrevivam às coletas gen#0) .

// Vamos tentar algo diferente.

var sum = 0d;
var count = 0;
string line;

// ID do filme Coração Valente como um Span;
var lookingFor = "110".AsSpan();

using (var fs = File.OpenRead(filePath))
using (var reader = new StreamReader(fs))
while ((line = reader.ReadLine()) != null)
{
    // Ignorando o ID do usuário 
    var span = line.AsSpan(line.IndexOf(',') + 1);

    // ID do filme
    var firstCommaPos = span.IndexOf(',');
    var movieId = span.Slice(0, firstCommaPos);
    if (!movieId.SequenceEqual(lookingFor)) continue;

    // Classificação
    span = span.Slice(firstCommaPos + 1);
    firstCommaPos = span.IndexOf(',');
    var rating = double.Parse(span.Slice(0, firstCommaPos), provider: CultureInfo.InvariantCulture);

    sum += rating;
    count++;
}

// O objetivo principal do código anterior era alocar menos objetos na memoria, reduzir a pressão no garba collector e obter melhor desempenho. Sucesso! Esse código é 4x mais rápido que o original, consome apenas 6 MB e exige ~ 50% menos coleções do garbage collector (Parabéns, Microsoft!).

// Porém, ainda estamos alocando um objeto string para cada linha do arquivo CSV. Vamos mudar isso.

var sum = 0d;
var count = 0;

var lookingFor = Encoding.UTF8.GetBytes("110").AsSpan();
var rawBuffer =  new byte[1024*1024];
using (var fs = File.OpenRead(filePath))
{
    var bytesBuffered = 0;
    var bytesConsumed = 0;

    while (true)
    {
        var bytesRead = fs.Read(rawBuffer, bytesBuffered, rawBuffer.Length - bytesBuffered);

        if (bytesRead == 0) break;
        bytesBuffered += bytesRead;

        int linePosition;

        do
        {
            linePosition = Array.IndexOf(rawBuffer, (byte) '\n', bytesConsumed,
                bytesBuffered - bytesConsumed);

            if (linePosition >= 0)
            {
                var lineLength = linePosition - bytesConsumed;
                var line = new Span<byte>(rawBuffer, bytesConsumed, lineLength);
                bytesConsumed += lineLength + 1;


                // Ignorando o ID do usuário
                var span = line.Slice(line.IndexOf((byte)',') + 1);

                // ID do filme
                var firstCommaPos = span.IndexOf((byte)',');
                var movieId = span.Slice(0, firstCommaPos);
                if (!movieId.SequenceEqual(lookingFor)) continue;

                // Classíficação
                span = span.Slice(firstCommaPos + 1);
                firstCommaPos = span.IndexOf((byte)',');
                var rating = double.Parse(Encoding.UTF8.GetString(span.Slice(0, firstCommaPos)), provider: CultureInfo.InvariantCulture);

                sum += rating;
                count++;
            }

        } while (linePosition >= 0 );

        Array.Copy(rawBuffer, bytesConsumed, rawBuffer, 0, (bytesBuffered - bytesConsumed));
        bytesBuffered -= bytesConsumed;
        bytesConsumed = 0;
    }
}
Console.WriteLine($"Classificação média para o filme Coração Valente é {sum/count} ({count}).");

// Desta vez, estamos carregando os dados em chunks de 1 MB. O código parece um pouco mais complexo (e é). Mas, ele é executado quase 10x mais rápido que a implementação original. Além disso, não há alocações suficientes para ativar o garbage collector.