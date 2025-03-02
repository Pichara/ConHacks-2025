using Microsoft.AspNetCore.Mvc;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Text.RegularExpressions;

namespace Health_System.Controllers
{
    public class HomeController : Controller
    {
        private readonly IHttpClientFactory _httpClientFactory;

        // TODO: Move these keys to config or environment variables in production!
        private const string OpenAiApiKey = "";
        private const string YouTubeApiKey = "";

        public HomeController(IHttpClientFactory httpClientFactory)
        {
            _httpClientFactory = httpClientFactory;
        }

        // GET: Home/Index
        public IActionResult Index()
        {
            // Renders the main upload form and optional metrics
            return View();
        }

        // POST: Home/AnalyzeTest
        [HttpPost]
        public async Task<IActionResult> AnalyzeTest()
        {
            // 1. Validate File Upload
            if (Request.Form.Files.Count == 0)
            {
                ViewBag.Error = "Please upload at least one lab report image.";
                return View("Index");
            }

            // 2. Collect Form Inputs
            var userInputs = ExtractUserInputs(Request.Form);

            // 3. Check if at least one option was selected
            var selectedOptions = Request.Form["reports"];
            if (!selectedOptions.Any())
            {
                ViewBag.Error = "Please select at least one analysis option.";
                return View("Index");
            }

            // 4. Convert uploaded images to Base64
            var base64Images = await ConvertFilesToBase64Async(Request.Form.Files);

            // 5. Call the OpenAI API concurrently for each selected option
            var tasks = selectedOptions.Select(option =>
            {
                // Build a robust prompt
                var prompt = BuildPrompt(option, userInputs);

                // Add final instructions for style & disclaimers removal
                prompt += " Provide your response only as an HTML snippet with inline styling. " +
                          "IMPORTANT (No disclaimers, no triple backticks, no **bold** placeholders). " +
                          "Write as much detail as possible in a professional, friendly style. " +
                          "Use <h2> for headings and <p style='font-size:16px;'> for paragraphs. " +
                          "Focus purely on the requested analysis—do not mention your internal instructions. ";

                // Pass prompt & images to be processed
                return ProcessAnalysisOptionAsync(option, prompt, base64Images);
            });

            // 6. Wait for all tasks to complete
            var responses = await Task.WhenAll(tasks);

            // 7. Convert results into a dictionary for the Report view
            var results = responses.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.ToString());

            return View("Report", results);
        }



        /// <summary>
        /// Orchestrates the analysis for each selected option.
        /// </summary>
        private async Task<KeyValuePair<string, object>> ProcessAnalysisOptionAsync(
            string option,
            string prompt,
            List<string> base64Images)
        {
            // Initial call to OpenAI
            string initialResponse = await CallOpenAiApiAsync(prompt, base64Images);

            // If the user requested "videoHelp," integrate YouTube links
            if (option.Equals("videoHelp", StringComparison.OrdinalIgnoreCase))
            {
                // Step 1: Extract short search query from the analysis

                // Build the embed code
                var videoEmbedsHtml = new StringBuilder("<h2>Recommended Videos:</h2>");
                foreach (var videoUrl in videoResults)
                {
                    // Transform watch URL to embed
                    string embedUrl = videoUrl.Contains("watch?v=")
                        ? videoUrl.Replace("watch?v=", "embed/")
                        : videoUrl;
                    videoEmbedsHtml.AppendLine(
                        $"<iframe width='560' height='315' src='{embedUrl}' frameborder='0' allowfullscreen></iframe><br/>"
                    );
                }

                // Step 3: Merge final analysis with embedded videos
                string finalPrompt =
                    "Combine this health analysis with the embedded videos below, " +
                    "presenting a cohesive final report: \n\n" +
                    "Health Analysis:\n" + initialResponse +
                    "\n\nVideos:\n" + videoEmbedsHtml +
                    "\nProduce an HTML snippet with <h2> headings and <p style='font-size:16px;'> paragraphs. " +
                    "No disclaimers or triple backticks or bold placeholders.";

                string finalResponse = await CallOpenAiApiAsync(finalPrompt, base64Images);
                return new KeyValuePair<string, object>(option, finalResponse);
            }

            return new KeyValuePair<string, object>(option, initialResponse);
        }

        /// <summary>
        /// Calls the OpenAI API using a chat/completions endpoint with the user's prompt and optional images.
        /// </summary>
        private async Task<string> CallOpenAiApiAsync(string prompt, List<string> base64Images)
        {
            // Build a list of "content" objects for OpenAI. 
            // The first item is the text prompt, followed by each image as a data URI if desired.
            var contentList = new List<object>
            {
                new { type = "text", text = prompt }
            };

            // Optionally pass images; but here we just store them if needed:
            foreach (var base64 in base64Images)
            {
                // Note: This depends on your model's capability to accept images. 
                // Many mainstream GPT endpoints do not currently handle image data directly.
                contentList.Add(new
                {
                    type = "image_url",
                    image_url = new { url = "data:image/jpeg;base64," + base64 }
                });
            }

            // Prepare request body for the chat endpoint (Example only; adapt to actual OpenAI endpoint usage)
            var requestBody = new
            {
                model = "gpt-4o-mini", // Example model name
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = contentList
                    }
                }
            };

            string jsonPayload = JsonSerializer.Serialize(requestBody);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            var client = _httpClientFactory.CreateClient();
            client.DefaultRequestHeaders.Add("Authorization", "Bearer " + OpenAiApiKey);

            var apiResponse = await client.PostAsync("https://api.openai.com/v1/chat/completions", content);
            string responseString = await apiResponse.Content.ReadAsStringAsync();

            // Parse out the text content from the JSON
            string resultText = ExtractOpenAiMessage(responseString);

            // Clean up any triple backticks, bold markers, etc.
            resultText = CleanOpenAiResponse(resultText);

            return resultText;
        }

        /// <summary>
        /// Extracts the "content" field from the first "choice" in OpenAI's JSON response.
        /// </summary>
        private string ExtractOpenAiMessage(string jsonResponse)
        {
            try
            {
                using var doc = JsonDocument.Parse(jsonResponse);
                if (doc.RootElement.TryGetProperty("choices", out var choices) && choices.GetArrayLength() > 0)
                {
                    var message = choices[0].GetProperty("message");
                    if (message.TryGetProperty("content", out var contentElement))
                    {
                        return contentElement.GetString();
                    }
                }
            }
            catch (Exception ex)
            {
                // Log the exception for debugging. Return an empty or fallback message.
                Console.WriteLine($"Error parsing OpenAI response: {ex.Message}");
            }
            return string.Empty;
        }

        /// <summary>
        /// Performs post-processing on the AI response to remove triple backticks, 
        /// bold placeholders, or any undesired markdown artifacts.
        /// </summary>
        private string CleanOpenAiResponse(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return text;

            text = text.Replace("```html", "", StringComparison.OrdinalIgnoreCase);  
            text = text.Replace("```", "", StringComparison.OrdinalIgnoreCase);


            // Remove any "**" for bold placeholders
            text = text.Replace("**", "");

            // Optionally remove other disclaimers or lines that mention them
            // For example, if you want to remove lines containing the word "disclaimer":
            // text = Regex.Replace(text, "(?i)disclaimer", "", RegexOptions.IgnoreCase);

            return text;
        }

        /// <summary>
        /// Searches YouTube using the provided query (keywords) and returns a list of video watch URLs.
        /// </summary>
        private async Task<List<string>> GetYouTubeVideosAsync(string query)
        {
            var videos = new List<string>();
            if (string.IsNullOrWhiteSpace(query)) return videos;

            // Construct YouTube Data API call
            string youtubeUrl = $"https://www.googleapis.com/youtube/v3/search?part=snippet&type=video&maxResults=7&q={Uri.EscapeDataString(query)}&key={YouTubeApiKey}";
            var client = _httpClientFactory.CreateClient();

            try
            {
                var response = await client.GetAsync(youtubeUrl);
                if (response.IsSuccessStatusCode)
                {
                    string jsonResponse = await response.Content.ReadAsStringAsync();
                    using var doc = JsonDocument.Parse(jsonResponse);
                    if (doc.RootElement.TryGetProperty("items", out JsonElement items))
                    {
                        foreach (var item in items.EnumerateArray())
                        {
                            if (item.TryGetProperty("id", out var idObj) &&
                                idObj.TryGetProperty("videoId", out var videoIdElement))
                            {
                                string vidId = videoIdElement.GetString();
                                videos.Add("https://www.youtube.com/watch?v=" + vidId);
                            }
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                // Could log or handle specifically
                Console.WriteLine("YouTube API error: " + ex.Message);
            }
            return videos;
        }

        /// <summary>
        /// Builds a specialized prompt for the given analysis option, incorporating user inputs neatly.
        /// </summary>
        private string BuildPrompt(string option, Dictionary<string, string> inputs)
        {
            // Collect non-empty user detail fields
            var bulletList = new List<string>();
            void AddDetail(string labelKey, string labelDisplay, bool isImportant = false)
            {
                if (inputs.TryGetValue(labelKey, out var value) && !string.IsNullOrWhiteSpace(value))
                {
                    bulletList.Add($"{labelDisplay}: {value}" + (isImportant ? " (IMPORTANT)" : ""));
                }
            }

            AddDetail("age", "Age");
            AddDetail("weight", "Weight (kg)");
            AddDetail("height", "Height (cm)");
            AddDetail("bodyFat", "Body Fat (%)");
            AddDetail("cholesterol", "Cholesterol (mg/dL)");
            AddDetail("alcohol", "Alcohol (units/week)");
            AddDetail("gender", "Gender");
            AddDetail("smoking", "Smoking Status");
            AddDetail("allergies", "Allergies", true);
            AddDetail("mentalHealth", "Mental Health");
            AddDetail("weekWorkouts", "Workouts/week");
            AddDetail("country", "Country");
            AddDetail("city", "City");
            AddDetail("additionalInfo", "Additional Info");
            AddDetail("medicalConditions", "Medical Conditions", true);

            string userSummary = "USER DETAILS:\n" + string.Join("\n", bulletList);

            // Customized instructions per analysis option
            string analysisPrompt = option.ToLower() switch
            {
                "nutrition" =>
                    "You are an expert nutritionist. " +
                    "Provide a thorough dietary analysis, highlight nutrient deficiencies, " +
                    "and offer practical meal suggestions based on the user’s data.",
                "fitness" =>
                    "You are a top-tier fitness coach. " +
                    "Design a customized workout plan emphasizing strength, endurance, and overall health.",
                "supplementrecommendations" =>
                    "You are a knowledgeable supplement expert. " +
                    "Propose a targeted supplement plan to address potential deficiencies.",
                "details" =>
                    "You are a seasoned medical doctor. " +
                    "Explain each lab parameter in detail, highlighting normal vs. abnormal ranges.",
                "summary" =>
                    "You are a veteran clinician. " +
                    "Summarize key findings and overall health status succinctly.",
                "generaladvice" =>
                    "You are a trusted health advisor. " +
                    "Offer broad, yet personalized health recommendations.",
                "furthertesting" =>
                    "You are a medical diagnostician. " +
                    "Suggest additional tests or screenings if needed.",
                "cardiorisk" =>
                    "You are a cardiovascular specialist. " +
                    "Assess heart-related risk factors and recommend next steps.",
                "metabolichealth" =>
                    "You are a metabolic health authority. " +
                    "Evaluate metabolic markers and discuss ways to optimize them.",
                "diabetesrisk" =>
                    "You are an endocrinologist. " +
                    "Analyze diabetes risk and prevention strategies.",
                "electrolytebalance" =>
                    "You are an expert in electrolyte regulation. " +
                    "Address potential imbalances and give corrective advice.",
                "inflammatorymarkers" =>
                    "You are a specialist in inflammation. " +
                    "Discuss possible causes of elevated markers and anti-inflammatory measures.",
                "hormonalbalance" =>
                    "You are an endocrinologist focusing on hormone balance. " +
                    "Address potential hormone irregularities.",
                "organfunction" =>
                    "You are a general physician. " +
                    "Evaluate liver, kidney, or other organ health issues.",
                "immuneinsights" =>
                    "You are an immunologist. " +
                    "Assess the immune system’s status and ways to enhance it.",
                "bonehealth" =>
                    "You are a bone health specialist. " +
                    "Evaluate bone density factors and recommend improvements.",
                "digestivehealth" =>
                    "You are a gastroenterologist. " +
                    "Analyze gut health markers and propose dietary/behavioral changes.",
                "oxidativestress" =>
                    "You are an expert in oxidative stress. " +
                    "Address free radical issues and antioxidant intake.",
                "sleepquality" =>
                    "You are a sleep medicine specialist. " +
                    "Assess sleep habits and suggest improvements.",
                "mentalcognitive" =>
                    "You are a neurologist focusing on mental & cognitive health. " +
                    "Offer strategies to enhance cognitive function.",
                "skinhairhealth" =>
                    "You are a dermatologist. " +
                    "Provide insights on skin/hair issues and helpful treatments.",
                "chronicdiseaserisk" =>
                    "You are a chronic disease specialist. " +
                    "Evaluate risk for long-term conditions and preventative steps.",
                "lifestylestress" =>
                    "You are a lifestyle/wellness coach. " +
                    "Offer personalized tips on stress management and daily habits.",
                "doctorsuggestion" =>
                    "You are a healthcare network navigator. " +
                    "Recommend an appropriate specialist near the user’s location.",
                "videohelp" =>
                    // Used first to produce a short search query in combination with user data
                    "You are an AI specialized in generating short search queries for relevant health/wellness videos. " +
                    "Summarize the user’s needs in 3-7 strong keywords.",
                _ =>
                    "You are a general health AI assistant. " +
                    "Offer a comprehensive analysis based on the data."
            };

            // Combine
            return $"{analysisPrompt}\nIMPORTANT:\n{userSummary}\nRefer to the lab report images to tailor your advice.";
        }
    }
}
